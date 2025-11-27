using Consul;
using Yarp.ReverseProxy.Configuration;
using YarpRouteConfig = Yarp.ReverseProxy.Configuration.RouteConfig;
using YarpClusterConfig = Yarp.ReverseProxy.Configuration.ClusterConfig;
using YarpDestinationConfig = Yarp.ReverseProxy.Configuration.DestinationConfig;
using YarpHealthCheckConfig = Yarp.ReverseProxy.Configuration.HealthCheckConfig;
using YarpActiveHealthCheckConfig = Yarp.ReverseProxy.Configuration.ActiveHealthCheckConfig;
using YarpPassiveHealthCheckConfig = Yarp.ReverseProxy.Configuration.PassiveHealthCheckConfig;
using YarpRouteMatch = Yarp.ReverseProxy.Configuration.RouteMatch;

namespace AspireDemo.Gateway;

/// <summary>
/// Consul 服务发现配置选项
/// </summary>
public class ConsulServiceDiscoveryOptions
{
    /// <summary>
    /// Consul 服务器地址
    /// </summary>
    public string Address { get; set; } = "http://localhost:8500";

    /// <summary>
    /// 刷新间隔 (秒)
    /// </summary>
    public int RefreshIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// 要监控的服务名称列表 (为空则监控所有服务)
    /// </summary>
    public List<string> ServiceNames { get; set; } = new();

    /// <summary>
    /// 服务名称到路由路径的映射
    /// </summary>
    public Dictionary<string, string> ServiceRouteMappings { get; set; } = new();
}

/// <summary>
/// 代理配置托管服务 - 从 Consul 获取服务信息并更新 YARP 网关配置
/// </summary>
internal class ProxyConfigHostedService : BackgroundService
{
    private readonly InMemoryConfigProvider _inMemoryConfigProvider;
    private readonly IConsulClient _consulClient;
    private readonly ILogger<ProxyConfigHostedService> _logger;
    private readonly IConfiguration _configuration;
    private readonly ConsulServiceDiscoveryOptions _options;

    // 缓存上一次的服务配置，用于检测变化
    private Dictionary<string, List<GatewayServiceInstance>> _lastServiceInstances = new();

    public ProxyConfigHostedService(
        InMemoryConfigProvider inMemoryConfigProvider,
        IConsulClient consulClient,
        ILogger<ProxyConfigHostedService> logger,
        IConfiguration configuration)
    {
        _inMemoryConfigProvider = inMemoryConfigProvider;
        _consulClient = consulClient;
        _logger = logger;
        _configuration = configuration;

        _options = new ConsulServiceDiscoveryOptions();
        configuration.GetSection("Consul").Bind(_options);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 等待一段时间让服务启动
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await UpdateProxyConfigurationAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating proxy configuration from Consul");
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.RefreshIntervalSeconds), stoppingToken);
        }
    }

    private async Task UpdateProxyConfigurationAsync(CancellationToken cancellationToken)
    {
        var allServices = await GetAllServicesFromConsulAsync(cancellationToken);

        // 检测是否有变化
        if (!HasServicesChanged(allServices))
        {
            return;
        }

        _lastServiceInstances = allServices;

        var routes = BuildRoutes(allServices);
        var clusters = BuildClusters(allServices);

        _inMemoryConfigProvider.Update(routes, clusters);

        _logger.LogInformation(
            "Updated YARP configuration with {RouteCount} routes and {ClusterCount} clusters",
            routes.Count, clusters.Count);
    }

    private async Task<Dictionary<string, List<GatewayServiceInstance>>> GetAllServicesFromConsulAsync(
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, List<GatewayServiceInstance>>();

        try
        {
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
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get services from Consul");
        }

        return result;
    }

    private async Task<List<GatewayServiceInstance>> GetServiceInstancesAsync(
        string serviceName,
        bool passingOnly,
        CancellationToken cancellationToken)
    {
        var queryResult = await _consulClient.Health.Service(serviceName, string.Empty, passingOnly, cancellationToken);

        return queryResult.Response
            .Select(entry => new GatewayServiceInstance
            {
                ServiceId = entry.Service.ID,
                ServiceName = entry.Service.Service,
                Address = entry.Service.Address,
                Port = entry.Service.Port,
                Tags = entry.Service.Tags?.ToList() ?? new List<string>(),
                Meta = entry.Service.Meta ?? new Dictionary<string, string>(),
                PathPrefix = GetMetaValue(entry.Service.Meta, "pathPrefix", string.Empty),
                Weight = int.TryParse(GetMetaValue(entry.Service.Meta, "weight", "1"), out var w) ? w : 1,
                Scheme = GetMetaValue(entry.Service.Meta, "scheme", "http"),
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

    private bool HasServicesChanged(Dictionary<string, List<GatewayServiceInstance>> newServices)
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

    private IReadOnlyList<YarpRouteConfig> BuildRoutes(Dictionary<string, List<GatewayServiceInstance>> services)
    {
        var routes = new List<YarpRouteConfig>();

        foreach (var service in services)
        {
            var serviceName = service.Key;
            var instances = service.Value;

            if (instances.Count == 0)
                continue;

            // 使用第一个实例的路径前缀作为路由匹配路径
            var pathPrefix = instances.FirstOrDefault()?.PathPrefix ?? string.Empty;
            var protocol = instances.FirstOrDefault()?.Protocol ?? "http";

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

            var transforms = new List<IReadOnlyDictionary<string, string>>();

            // 去除服务名称前缀的转换
            if (string.IsNullOrEmpty(pathPrefix))
            {
                transforms.Add(new Dictionary<string, string>
                {
                    ["PathRemovePrefix"] = $"/api/{serviceName}"
                });
            }

            var routeConfig = new YarpRouteConfig
            {
                RouteId = $"route-{serviceName}",
                ClusterId = $"cluster-{serviceName}",
                Match = new YarpRouteMatch
                {
                    Path = routePath
                },
                Transforms = transforms.Count > 0 ? transforms : null,
                Metadata = new Dictionary<string, string>
                {
                    ["protocol"] = protocol
                }
            };

            routes.Add(routeConfig);
        }

        // 添加默认的 catch-all 路由 (可选)
        if (services.Count > 0)
        {
            // 获取第一个服务作为默认后端
            var defaultService = services.FirstOrDefault();
            if (defaultService.Value?.Count > 0)
            {
                routes.Add(new YarpRouteConfig
                {
                    RouteId = "route-default",
                    ClusterId = $"cluster-{defaultService.Key}",
                    Order = int.MaxValue, // 最低优先级
                    Match = new YarpRouteMatch
                    {
                        Path = "{**catch-all}"
                    }
                });
            }
        }

        return routes;
    }

    private IReadOnlyList<YarpClusterConfig> BuildClusters(Dictionary<string, List<GatewayServiceInstance>> services)
    {
        var clusters = new List<YarpClusterConfig>();

        foreach (var service in services)
        {
            var serviceName = service.Key;
            var instances = service.Value;

            if (instances.Count == 0)
                continue;

            var destinations = new Dictionary<string, YarpDestinationConfig>();

            foreach (var instance in instances)
            {
                var destinationId = instance.ServiceId;
                destinations[destinationId] = new YarpDestinationConfig
                {
                    Address = instance.GetServiceUrl(),
                    Metadata = new Dictionary<string, string>
                    {
                        ["weight"] = instance.Weight.ToString(),
                        ["protocol"] = instance.Protocol
                    }
                };
            }

            var clusterConfig = new YarpClusterConfig
            {
                ClusterId = $"cluster-{serviceName}",
                Destinations = destinations,
                LoadBalancingPolicy = GetLoadBalancingPolicy(instances),
                HealthCheck = new YarpHealthCheckConfig
                {
                    Active = new YarpActiveHealthCheckConfig
                    {
                        Enabled = true,
                        Interval = TimeSpan.FromSeconds(30),
                        Timeout = TimeSpan.FromSeconds(10),
                        Path = "/health"
                    },
                    Passive = new YarpPassiveHealthCheckConfig
                    {
                        Enabled = true,
                        Policy = "TransportFailureRateHealthPolicy",
                        ReactivationPeriod = TimeSpan.FromSeconds(60)
                    }
                },
                Metadata = new Dictionary<string, string>
                {
                    ["serviceName"] = serviceName
                }
            };

            clusters.Add(clusterConfig);
        }

        return clusters;
    }

    private static string GetLoadBalancingPolicy(List<GatewayServiceInstance> instances)
    {
        // 如果所有实例的权重都相同，使用轮询
        var weights = instances.Select(i => i.Weight).Distinct().ToList();
        if (weights.Count == 1)
        {
            return "RoundRobin";
        }

        // 如果有不同的权重，使用 PowerOfTwoChoices
        return "PowerOfTwoChoices";
    }
}

/// <summary>
/// 网关服务实例信息
/// </summary>
public class GatewayServiceInstance
{
    public string ServiceId { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public int Port { get; set; }
    public List<string> Tags { get; set; } = new();
    public IDictionary<string, string> Meta { get; set; } = new Dictionary<string, string>();
    public string PathPrefix { get; set; } = string.Empty;
    public int Weight { get; set; } = 1;
    public string Scheme { get; set; } = "http";
    public string Protocol { get; set; } = "http";

    public string GetServiceUrl() => $"{Scheme}://{Address}:{Port}";
}