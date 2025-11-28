using Microsoft.Extensions.Hosting;

namespace AspireDemo.ServiceDefaults.Consul;

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
    /// 在多网卡环境中指定优先使用的网络段 ,支持以指定前缀开头（如 "192.168."）或 匹配正则表达式模式; 
    /// 多个网络段用逗号分隔
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
