using AspireDemo.ServiceDefaults.Consul;
using Consul;
using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.LoadBalancing;
// 使用别名解决命名空间冲突
using ClusterConfig = Yarp.ReverseProxy.Configuration.ClusterConfig;
using DestinationConfig = Yarp.ReverseProxy.Configuration.DestinationConfig;
using HttpClientConfig = Yarp.ReverseProxy.Configuration.HttpClientConfig;
using RouteConfig = Yarp.ReverseProxy.Configuration.RouteConfig;
using RouteMatch = Yarp.ReverseProxy.Configuration.RouteMatch;

namespace AspireDemo.Gateway;

/// <summary>
/// Consul 配置提供程序 - 实现 IProxyConfigProvider 接口，支持 YARP 配置实时更新
/// </summary>
public class ConsulConfigProvider : IProxyConfigProvider, IHostedService, IDisposable
{
    private readonly IConsulClient _consulClient;
    private readonly ILogger<ConsulConfigProvider> _logger;
    private readonly IConfiguration _configuration;
    private readonly ConsulServiceDiscoveryOptions _options;
    private volatile ConsulProxyConfig _currentConfig;
    private CancellationTokenSource? _stoppingCts;
    private Task? _executingTask;

    /// <summary>
    /// 缓存上一次的服务配置，用于检测变化
    /// </summary>
    private Dictionary<string, List<ConsulServiceInstance>> _lastServiceInstances = new();

    public ConsulConfigProvider(
        IConsulClient consulClient,
        ILogger<ConsulConfigProvider> logger,
        IConfiguration configuration)
    {
        _consulClient = consulClient;
        _logger = logger;
        _configuration = configuration;

        _options = new ConsulServiceDiscoveryOptions();
        configuration.GetSection("Consul").Bind(_options);

        // 初始化一个空的配置，但带有可触发的变更通知
        _currentConfig = new ConsulProxyConfig();
    }

    /// <summary>
    /// 获取当前的代理配置
    /// </summary>
    public IProxyConfig GetConfig()
    {
        return _currentConfig;
    }

    #region IHostedService 实现

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 启动后台任务
        _executingTask = ExecuteAsync(_stoppingCts.Token);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_executingTask == null)
        {
            return;
        }

        try
        {
            _stoppingCts?.Cancel();
        }
        finally
        {
            await Task.WhenAny(_executingTask, Task.Delay(Timeout.Infinite, cancellationToken));
        }
    }

    public void Dispose()
    {
        _stoppingCts?.Cancel();
        _stoppingCts?.Dispose();
    }

    #endregion

    private async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 立即尝试加载配置
        try
        {
            await LoadConfigFromConsulAsync(stoppingToken);
            _logger.LogInformation("Initial proxy configuration loaded successfully");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load initial proxy configuration, will retry");
        }

        // 定期刷新配置
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.RefreshIntervalSeconds), stoppingToken);
                await LoadConfigFromConsulAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // 正常取消，退出循环
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating proxy configuration from Consul");
            }
        }
    }

    /// <summary>
    /// 从 Consul 加载配置
    /// </summary>
    public async Task LoadConfigFromConsulAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var allServices = await GetAllServicesFromConsulAsync(cancellationToken);
            // 检测是否需要更新
            if (HasServicesChanged(allServices))
            {
                // 构建新的路由和集群配置
                var newRoutes = BuildRoutes(allServices);
                var newClusters = BuildClusters(allServices);

                // 1. 创建新配置
                var newConfig = new ConsulProxyConfig(newRoutes, newClusters);

                // 2. 原子交换并取回旧配置
                var oldConfig = Interlocked.Exchange(ref _currentConfig, newConfig);

                // 3. 触发旧配置的 ChangeToken，通知 YARP 来获取新配置
                oldConfig.SignalChange();

                _lastServiceInstances = allServices;

                _logger.LogInformation(
                    "Updated proxy config with {RouteCount} routes and {ClusterCount} clusters",
                    newRoutes.Count, newClusters.Count);

                // 记录所有路由配置用于调试
                foreach (var route in newRoutes)
                {
                    _logger.LogDebug("Route: {RouteId} -> {Path} -> {ClusterId}",
                        route.RouteId, route.Match.Path, route.ClusterId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load config from Consul");
            throw;
        }
    }

    #region 从 Consul 获取服务

    private async Task<Dictionary<string, List<ConsulServiceInstance>>> GetAllServicesFromConsulAsync(
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, List<ConsulServiceInstance>>();

        // 获取所有服务
        var services = await _consulClient.Catalog.Services(cancellationToken);

        foreach (var service in services.Response)
        {
            var serviceName = service.Key;

            // 跳过 consul 本身
            if (serviceName.Equals("consul", StringComparison.OrdinalIgnoreCase))
                continue;

            // 如果配置了特定服务名称列表，则只获取列表中的服务
            if (_options.ServiceNames.Count > 0 &&
                !_options.ServiceNames.Contains(serviceName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var instances = await GetServiceInstancesAsync(serviceName, true, cancellationToken);
            if (instances.Count > 0)
            {
                result[serviceName] = instances;
            }
        }

        _logger.LogDebug("Found {ServiceCount} services from Consul: {Services}",
            result.Count, string.Join(", ", result.Keys));

        return result;
    }

    private async Task<List<ConsulServiceInstance>> GetServiceInstancesAsync(
        string serviceName,
        bool passingOnly,
        CancellationToken cancellationToken)
    {
        var queryResult = await _consulClient.Health.Service(serviceName, string.Empty, passingOnly, cancellationToken);

        return queryResult.Response
            .Select(entry => new ConsulServiceInstance
            {
                ServiceId = $"{serviceName}-{entry.Service.Address}-{entry.Service.Port}-{entry.Service.ID}",
                ServiceName = entry.Service.Service,
                Address = entry.Service.Address,
                Port = entry.Service.Port,
                Tags = entry.Service.Tags?.ToList() ?? new List<string>(),
                Meta = entry.Service.Meta ?? new Dictionary<string, string>(),
                PathPrefix = GetMetaValue(entry.Service.Meta, "pathPrefix", string.Empty),
                Weight = int.TryParse(GetMetaValue(entry.Service.Meta, "weight", "1"), out var w) ? w : 1,
                Scheme = GetMetaValue(entry.Service.Meta, "scheme", "https"),
                Protocol = GetMetaValue(entry.Service.Meta, "protocol", "http")
            })
            .ToList();
    }

    private static string GetMetaValue(IDictionary<string, string>? meta, string key, string defaultValue)
    {
        if (meta == null || !meta.TryGetValue(key, out var value))
        {
            return defaultValue;
        }
        return value ?? defaultValue;
    }

    #endregion

    #region 构建路由和集群配置

    private List<RouteConfig> BuildRoutes(Dictionary<string, List<ConsulServiceInstance>> services)
    {
        var routes = new List<RouteConfig>();

        foreach (var service in services)
        {
            var serviceName = service.Key;
            var instances = service.Value;

            if (instances.Count == 0)
                continue;

            // 使用第一个实例的路径前缀作为路由匹配路径
            var pathPrefix = instances.FirstOrDefault()?.PathPrefix ?? string.Empty;
            var protocol = instances.FirstOrDefault()?.Protocol ?? "http";

            var transforms = new List<IReadOnlyDictionary<string, string>>();
            // 检查是否有自定义路由映射
            string routePath;
            if (_options.ServiceRouteMappings.TryGetValue(serviceName, out var customPath))
            {
                routePath = customPath;
            }
            else if (!string.IsNullOrEmpty(pathPrefix))
            {
                routePath = $"{pathPrefix}/{{**catch-all}}";
            }
            else
            {
                routePath = $"/api/{serviceName}/{{**catch-all}}";
            }

            var routeConfig = new RouteConfig
            {
                RouteId = $"route-{serviceName}",
                ClusterId = $"cluster-{serviceName}",
                Match = new RouteMatch
                {
                    Path = routePath
                },
                Metadata = new Dictionary<string, string>
                {
                    ["protocol"] = protocol
                }
            };

            routes.Add(routeConfig);
        }

        return routes;
    }

    private List<ClusterConfig> BuildClusters(Dictionary<string, List<ConsulServiceInstance>> services)
    {
        var clusters = new List<ClusterConfig>();

        foreach (var service in services)
        {
            var serviceName = service.Key;
            var instances = service.Value;

            if (instances.Count == 0)
                continue;

            var destinations = new Dictionary<string, DestinationConfig>();

            foreach (var instance in instances)
            {
                var destinationId = instance.ServiceId;
                destinations[destinationId] = new DestinationConfig
                {
                    Address = instance.GetServiceUrl(),
                    Metadata = new Dictionary<string, string>
                    {
                        ["weight"] = instance.Weight.ToString(),
                        ["protocol"] = instance.Protocol
                    }
                };
            }

            var clusterConfig = new ClusterConfig
            {
                ClusterId = $"cluster-{serviceName}",
                Destinations = destinations,
                LoadBalancingPolicy = GetLoadBalancingPolicy(instances),
                HttpClient = new HttpClientConfig
                {
                    DangerousAcceptAnyServerCertificate = true
                },
                //HealthCheck = new HealthCheckConfig
                //{
                //    Active = new ActiveHealthCheckConfig
                //    {
                //        Enabled = true,
                //        Interval = TimeSpan.FromSeconds(10),  // 更频繁的检查（10秒）
                //        Timeout = TimeSpan.FromSeconds(5),    // 更短的超时（5秒）
                //        Path = "/health"
                //    },
                //    Passive = new PassiveHealthCheckConfig
                //    {
                //        Enabled = true,
                //        Policy = "TransportFailureRateHealthPolicy",
                //        ReactivationPeriod = TimeSpan.FromSeconds(30)  // 更快的恢复尝试（30秒）
                //    }
                //},
                Metadata = new Dictionary<string, string>
                {
                    ["serviceName"] = serviceName
                }
            };

            clusters.Add(clusterConfig);
        }

        return clusters;
    }

    private static string GetLoadBalancingPolicy(List<ConsulServiceInstance> instances)
    {
        // 如果所有实例的权重都相同，使用轮询
        var weights = instances.Select(i => i.Weight).Distinct().ToList();
        if (weights.Count == 1)
        {
            return LoadBalancingPolicies.RoundRobin;
        }
        // 如果权重不同，可以考虑使用加权负载均衡（需要自定义实现）
        return LoadBalancingPolicies.RoundRobin;
    }

    #endregion

    #region 配置变更检测

    private bool HasServicesChanged(Dictionary<string, List<ConsulServiceInstance>> newServices)
    {
        if (_lastServiceInstances.Count != newServices.Count)
            return true;

        foreach (var kvp in newServices)
        {
            if (!_lastServiceInstances.TryGetValue(kvp.Key, out var oldInstances))
                return true;

            if (oldInstances.Count != kvp.Value.Count)
                return true;

            var oldIds = oldInstances.Select(i => i.ServiceId).OrderBy(x => x).ToList();
            var newIds = kvp.Value.Select(i => i.ServiceId).OrderBy(x => x).ToList();

            if (!oldIds.SequenceEqual(newIds))
                return true;
        }

        return false;
    }


    #endregion
}

/// <summary>
/// Consul 代理配置 - 封装 IProxyConfig 和变更通知逻辑
/// </summary>
internal class ConsulProxyConfig : IProxyConfig
{
    private readonly CancellationTokenSource _cts = new();

    public ConsulProxyConfig(List<RouteConfig>? routes = null, List<ClusterConfig>? clusters = null)
    {
        Routes = routes ?? new List<RouteConfig>();
        Clusters = clusters ?? new List<ClusterConfig>();
        ChangeToken = new CancellationChangeToken(_cts.Token);
    }

    public IReadOnlyList<RouteConfig> Routes { get; }
    public IReadOnlyList<ClusterConfig> Clusters { get; }
    public IChangeToken ChangeToken { get; }

    /// <summary>
    /// 触发变更通知，通知 YARP 重新获取配置
    /// </summary>
    internal void SignalChange()
    {
        _cts.Cancel();
    }
}
