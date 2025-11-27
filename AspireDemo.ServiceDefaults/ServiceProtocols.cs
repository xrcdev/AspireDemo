namespace Microsoft.Extensions.Hosting;

/// <summary>
/// 服务协议类型常量定义
/// </summary>
public static class ServiceProtocolTypes
{
    /// <summary>
    /// HTTP 协议 (包括 HTTP 和 HTTPS)
    /// </summary>
    public static readonly string Http = "http";

    ///// <summary>
    ///// HTTPS 协议
    ///// </summary>
    //public static readonly string Https = "https";

    /// <summary>
    /// gRPC 协议
    /// </summary>
    public static readonly string Grpc = "grpc";

    /// <summary>
    /// WebSocket 协议
    /// </summary>
    public static readonly string WebSocket = "websocket";

    /// <summary>
    /// TCP 协议
    /// </summary>
    public static readonly string Tcp = "tcp";

    /// <summary>
    /// UDP 协议
    /// </summary>
    public static readonly string Udp = "udp";

    /// <summary>
    /// 所有支持的协议类型
    /// </summary>
    public static readonly IReadOnlyList<string> AllProtocols = new List<string>
    {
        Http,
        Grpc,
        WebSocket,
        Tcp,
        Udp
    }.AsReadOnly();

    /// <summary>
    /// 验证协议类型是否有效
    /// </summary>
    /// <param name="protocol">协议类型</param>
    /// <returns>是否有效</returns>
    public static bool IsValid(string protocol)
    {
        return AllProtocols.Contains(protocol, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 健康检查路径常量定义
/// </summary>
public static class HealthCheckPaths
{
    /// <summary>
    /// 标准健康检查端点路径 /health
    /// </summary>
    public static readonly string Health = "/health";

    /// <summary>
    /// 存活性检查端点路径 /live
    /// </summary>
    public static readonly string Live = "/live";

    /// <summary>
    /// 就绪性检查端点路径 /ready
    /// </summary>
    public static readonly string Ready = "/ready";
}


