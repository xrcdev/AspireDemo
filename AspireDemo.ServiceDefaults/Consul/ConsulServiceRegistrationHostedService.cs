using Consul;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace AspireDemo.ServiceDefaults.Consul;

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
    /// 路径前缀 (最终作为元数据中的项)
    /// </summary>
    public string PathPrefix { get; set; } = string.Empty;

    /// <summary>
    /// 权重 (最终作为元数据中的项)
    /// </summary>
    public int Weight { get; set; } = 1;

    /// <summary>
    /// Scheme (http/https) (最终作为元数据中的项)
    /// </summary>
    public string Scheme { get; set; } = "http";

    /// <summary>
    /// 协议类型 (http, grpc, websocket等) (最终作为元数据中的项)
    /// </summary>
    public string Protocol { get; set; } = "http";

    /// <summary>
    /// 获取完整的服务 URL
    /// </summary>
    public string GetServiceUrl() => $"{Scheme}://{Address}:{Port}";
}
