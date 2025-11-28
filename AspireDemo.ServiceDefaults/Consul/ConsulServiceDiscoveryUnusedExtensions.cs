using Consul;

namespace AspireDemo.ServiceDefaults.Consul;

/// <summary>
/// Consul 服务发现客户端扩展
/// </summary>
public static class ConsulServiceDiscoveryUnusedExtensions
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
