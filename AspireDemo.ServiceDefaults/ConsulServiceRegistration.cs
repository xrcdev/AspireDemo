using Consul;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Consul 服务注册配置选项
/// </summary>
public class ConsulServiceRegistrationOptions
{
    
    /// <summary>
    /// 服务名称
    /// </summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>
    /// 服务地址 (可选,默认自动获取)
    /// </summary>
    public string? ServiceAddress { get; set; }

    /// <summary>
    /// 服务端口 (可选,默认自动获取)
    /// </summary>
    public int? ServicePort { get; set; }

    /// <summary>
    /// 在多网卡环境中指定优先使用的网络段 ,支持以指定前缀开头（如 "192.168."）或 匹配正则表达式模式; 多个网络段用逗号分隔
    /// </summary>
    public string? PreferredNetworks { get; set; }

    

    /// <summary>
    /// 服务的地址前缀 (如 /api/v1)
    /// </summary>
    public string? PathPrefix { get; set; } = "";

    /// <summary>
    /// 服务权重 (用于负载均衡)
    /// </summary>
    public int Weight { get; set; } = 1;

    
    /// <summary>
    /// 服务协议类型 (http(包括http和https), grpc, websocket 等)
    /// </summary>
    public string Protocol { get; set; } = ServiceProtocolTypes.Http;

    /// <summary>
    /// 服务的 Scheme (http/https)
    /// </summary>
    public string HttpScheme { get; set; } = "http";


    /// <summary>
    /// 健康检查路径
    /// </summary>
    public string HealthCheckPath { get; set; } = HealthCheckPaths.Health;

    /// <summary>
    /// 健康检查间隔 (秒)
    /// </summary>
    public int HealthCheckIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// 健康检查超时 (秒)
    /// </summary>
    public int HealthCheckTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// 服务注销前的超时时间 (秒)
    /// </summary>
    public int DeregisterCriticalServiceAfterSeconds { get; set; } = 30;

    /// <summary>
    /// 额外的元数据
    /// </summary>
    public Dictionary<string, string> Meta { get; set; } = new();

    /// <summary>
    /// 服务标签
    /// </summary>
    public List<string> Tags { get; set; } = new();


    /// <summary>
    /// Consul 服务器地址
    /// </summary>
    public string ConsulAddress { get; set; } = "http://localhost:8500";

}

/// <summary>
/// Consul 服务注册扩展方法
/// </summary>
public static class ConsulServiceRegistrationExtensions
{
    /// <summary>
    /// 添加 Consul 服务注册支持
    /// </summary>
    /// <param name="builder">Host 应用程序构建器</param>
    /// <param name="configure">配置选项</param>
    /// <returns>构建器</returns>
    public static TBuilder AddConsulServiceRegistration<TBuilder>(
        this TBuilder builder,
        Action<ConsulServiceRegistrationOptions>? configure = null) where TBuilder : IHostApplicationBuilder
    {
        var options = new ConsulServiceRegistrationOptions();

        // 从配置中读取
        builder.Configuration.GetSection("Consul").Bind(options);

        // 应用自定义配置
        configure?.Invoke(options);

        // 如果服务名称为空，使用应用程序名称
        if (string.IsNullOrEmpty(options.ServiceName))
        {
            options.ServiceName = builder.Environment.ApplicationName;
        }

        // 注册配置
        builder.Services.AddSingleton(options);

        // 注册 Consul 客户端
        builder.Services.AddSingleton<IConsulClient>(sp =>
        {
            var opts = sp.GetRequiredService<ConsulServiceRegistrationOptions>();
            return new ConsulClient(config =>
            {
                config.Address = new Uri(opts.ConsulAddress);
            });
        });

        // 注册托管服务用于服务注册/注销
        builder.Services.AddHostedService<ConsulServiceRegistrationHostedService>();

        return builder;
    }

    /// <summary>
    /// 使用 Consul 服务注册 (在 WebApplication 启动时)
    /// </summary>
    /// <param name="app">Web 应用程序</param>
    /// <returns>Web 应用程序</returns>
    public static WebApplication UseConsulServiceRegistration(this WebApplication app)
    {
        // 托管服务会自动处理注册，这里可以添加额外的中间件逻辑
        return app;
    }
}

/// <summary>
/// Consul 服务注册托管服务
/// </summary>
internal class ConsulServiceRegistrationHostedService : IHostedService
{
    private readonly IConsulClient _consulClient;
    private readonly ConsulServiceRegistrationOptions _options;
    private readonly ILogger<ConsulServiceRegistrationHostedService> _logger;
    private readonly IServer _server;
    private readonly IHostEnvironment _environment;
    private readonly IHostApplicationLifetime _lifetime;
    private string? _serviceId;

    public ConsulServiceRegistrationHostedService(
        IConsulClient consulClient,
        ConsulServiceRegistrationOptions options,
        ILogger<ConsulServiceRegistrationHostedService> logger,
        IServer server,
        IHostEnvironment environment,
        IHostApplicationLifetime lifetime)
    {
        _consulClient = consulClient;
        _options = options;
        _logger = logger;
        _server = server;
        _environment = environment;
        _lifetime = lifetime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 注册在应用程序启动完成后执行
        _lifetime.ApplicationStarted.Register(() =>
        {
            _ = RegisterToConsulAsync();
        });

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_serviceId))
            return;

        try
        {
            await _consulClient.Agent.ServiceDeregister(_serviceId, cancellationToken);
            _logger.LogInformation("Service deregistered from Consul: {ServiceId}", _serviceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deregister service from Consul: {ServiceId}", _serviceId);
        }
    }

    private async Task RegisterToConsulAsync()
    {
        try
        {
            var (address, port, scheme) = GetServiceAddressAndPort();

            _serviceId = $"{_options.ServiceName}-{address}-{port}-{Guid.NewGuid():N}";

            // 构建元数据
            var meta = new Dictionary<string, string>(_options.Meta)
            {
                ["pathPrefix"] = _options.PathPrefix ?? string.Empty,
                ["weight"] = _options.Weight.ToString(),
                ["scheme"] = scheme,
                ["protocol"] = _options.Protocol,
                ["environment"] = _environment.EnvironmentName
            };

            // 构建健康检查
            var healthCheckUrl = $"{scheme}://{address}:{port}{_options.HealthCheckPath}";

            var registration = new AgentServiceRegistration
            {
                ID = _serviceId,
                Name = _options.ServiceName,
                Address = address,
                Port = port,
                Tags = _options.Tags.ToArray(),
                Meta = meta,
                Check = new AgentServiceCheck
                {
                    HTTP = healthCheckUrl,
                    Interval = TimeSpan.FromSeconds(_options.HealthCheckIntervalSeconds),
                    Timeout = TimeSpan.FromSeconds(_options.HealthCheckTimeoutSeconds),
                    DeregisterCriticalServiceAfter = TimeSpan.FromSeconds(_options.DeregisterCriticalServiceAfterSeconds),
                    TLSSkipVerify = true // 开发环境可能使用自签名证书
                }
            };

            await _consulClient.Agent.ServiceRegister(registration);
            _logger.LogInformation(
                "Service registered to Consul: {ServiceName} ({ServiceId}) at {Address}:{Port}",
                _options.ServiceName, _serviceId, address, port);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register service to Consul: {ServiceName}", _options.ServiceName);
        }
    }

    private (string address, int port, string scheme) GetServiceAddressAndPort()
    {
        // 优先使用配置的地址和端口
        if (!string.IsNullOrEmpty(_options.ServiceAddress) && _options.ServicePort.HasValue)
        {
            return (_options.ServiceAddress, _options.ServicePort.Value, _options.HttpScheme);
        }

        // 尝试从服务器功能获取地址
        var features = _server.Features;
        var addresses = features.Get<IServerAddressesFeature>();

        if (addresses?.Addresses != null && addresses.Addresses.Any())
        {
            foreach (var item in addresses.Addresses)
            {
                // 替换通配符地址为真实 IP
                var addressUri = new Uri(ReplaceAddress(item, _options.PreferredNetworks));
                
                // 确保不使用 127.0.0.1 或 localhost,因为 Consul 无法访问
                var host = addressUri.Host;
                if (host == "127.0.0.1" || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                {
                    host = GetCurrentIp(_options.PreferredNetworks ?? string.Empty);
                }
                
                return (host, addressUri.Port, addressUri.Scheme);
            }
        }

        // 默认值
        _logger.LogWarning("Unable to get server addresses from IServerAddressesFeature, using fallback IP address");
        return (
            _options.ServiceAddress ?? GetCurrentIp(_options.PreferredNetworks ?? string.Empty),
            _options.ServicePort ?? 80,
            _options.HttpScheme
        );
    }

    private static string ReplaceAddress(string address, string? preferredNetworks)
    {
        var ip = GetCurrentIp(preferredNetworks ?? string.Empty);

        if (address.Contains("*"))
        {
            address = address.Replace("*", ip);
        }
        else if (address.Contains("+"))
        {
            address = address.Replace("+", ip);
        }
        else if (address.Contains("localhost", StringComparison.OrdinalIgnoreCase))
        {
            address = address.Replace("localhost", ip, StringComparison.OrdinalIgnoreCase);
        }
        else if (address.Contains("0.0.0.0", StringComparison.OrdinalIgnoreCase))
        {
            address = address.Replace("0.0.0.0", ip, StringComparison.OrdinalIgnoreCase);
        }

        return address;
    }

    private static string GetCurrentIp(string preferredNetworks)
    {
        var instanceIp = "127.0.0.1";

        try
        {
            // 获取可用网卡
            var nics = NetworkInterface.GetAllNetworkInterfaces()?.Where(network => network.OperationalStatus == OperationalStatus.Up);

            // 获取所有可用网卡IP信息
            var ipCollection = nics?.Select(x => x.GetIPProperties())?.SelectMany(x => x.UnicastAddresses);

            var preferredNetworksArr = string.IsNullOrWhiteSpace(preferredNetworks)
                ? Array.Empty<string>()
                : preferredNetworks.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var ipadd in ipCollection)
            {
                if (!IPAddress.IsLoopback(ipadd.Address) &&
                    ipadd.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    if (preferredNetworksArr.Length == 0)
                    {
                        instanceIp = ipadd.Address.ToString();
                        break;
                    }

                    /*
                    • 服务需要向 Consul 注册自己的真实IP地址
                      • 在多网卡环境中需要选择正确的网卡IP
                      • 支持通过 preferredNetworks 参数指定优先使用的网络段
                      • 确保注册的IP地址是其他服务可以访问的有效地址
                      • 检查IP是否以指定前缀开头（如 "192.168."）
                      • 或者匹配正则表达式模式
                     */
                    if (!preferredNetworksArr.Any(preferredNetwork =>
                            ipadd.Address.ToString().StartsWith(preferredNetwork)
                            || Regex.IsMatch(ipadd.Address.ToString(), preferredNetwork))) continue;
                    instanceIp = ipadd.Address.ToString();
                    break;
                }
            }
        }
        catch
        {
            // ignored
        }

        return instanceIp;
    }
}

/// <summary>
/// Consul 服务发现客户端扩展
/// </summary>
public static class ConsulServiceDiscoveryExtensions
{
    /// <summary>
    /// 从 Consul 获取服务实例列表
    /// </summary>
    /// <param name="consulClient">Consul 客户端</param>
    /// <param name="serviceName">服务名称</param>
    /// <param name="passingOnly">是否只返回健康的服务</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>服务实例列表</returns>
    public static async Task<IReadOnlyList<ConsulServiceInstance>> GetServiceInstancesAsync(
        this IConsulClient consulClient,
        string serviceName,
        bool passingOnly = true,
        CancellationToken cancellationToken = default)
    {
        var result = await consulClient.Health.Service(serviceName, string.Empty, passingOnly, cancellationToken);

        return result.Response
            .Select(entry => new ConsulServiceInstance
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
}

/// <summary>
/// Consul 服务实例信息
/// </summary>
public class ConsulServiceInstance
{
    /// <summary>
    /// 服务实例 ID
    /// </summary>
    public string ServiceId { get; set; } = string.Empty;

    /// <summary>
    /// 服务名称
    /// </summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>
    /// 服务地址
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// 服务端口
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// 服务标签
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// 元数据
    /// </summary>
    public IDictionary<string, string> Meta { get; set; } = new Dictionary<string, string>();

    /// <summary>
    /// 路径前缀
    /// </summary>
    public string PathPrefix { get; set; } = string.Empty;

    /// <summary>
    /// 权重
    /// </summary>
    public int Weight { get; set; } = 1;

    /// <summary>
    /// Scheme (http/https)
    /// </summary>
    public string Scheme { get; set; } = "http";

    /// <summary>
    /// 协议类型 (http, grpc, websocket)
    /// </summary>
    public string Protocol { get; set; } = "http";

    /// <summary>
    /// 获取完整的服务 URL
    /// </summary>
    public string GetServiceUrl() => $"{Scheme}://{Address}:{Port}{PathPrefix}";
}
