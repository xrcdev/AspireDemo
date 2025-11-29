using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Google.Protobuf.WellKnownTypes;


var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedis("cache")
    .WithImageTag("8.2")
    .PublishAsConnectionString(); // 添加发布支持

var consul = AddConsul(builder)
    .PublishAsContainer();

var apiService = builder.AddProject<Projects.AspireDemo_ApiService>("apiservice")
    .WithHttpHealthCheck("/health")
    //.WithReference(consul)
    //.WithAnnotation(new ResourceRelationshipAnnotation(consul.Resource, "Reference"))
    .WaitFor(consul)
    .WithEnvironment("CONSUL_ADDRESS", consul.GetEndpoint("http"))
    .WithReplicas(2)
    .PublishAsDockerFile(); // 生成 Dockerfile;

var gateway = builder.AddProject<Projects.AspireDemo_Gateway>("gateway")
    .WithEnvironment("CONSUL_ADDRESS", consul.GetEndpoint("http"))
    .WaitFor(consul);

var webfrontend = builder.AddProject<Projects.AspireDemo_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(cache)
    .WaitFor(cache)
    .WithReference(apiService)
    .WaitFor(apiService)
    .WithReference(gateway)
    .WaitFor(gateway)
    .PublishAsDockerFile();

//var scalar= builder.AddScalarApiReference();
//scalar.WithReference(apiService);
// 是调试模式 ,还是发布清单模式
if (builder.ExecutionContext.IsRunMode)
{

}

builder.Build().Run();


/// <summary>
/// 添加 Consul 服务发现与配置中心容器到 Aspire 应用程序
/// </summary>
/// <param name="builder">分布式应用程序构建器</param>
/// <returns>配置好的 Consul 容器资源构建器</returns>
/// <remarks>
/// 此方法配置 Consul 服务器容器,包括:
/// <list type="bullet">
/// <item><description>使用开发模式运行 (dev mode)</description></item>
/// <item><description>允许任何客户端连接</description></item>
/// <item><description>暴露 HTTP (8500) 和 DNS (8600) 端点</description></item>
/// <item><description>持久化数据到 consul-data 卷</description></item>
/// <item><description>使用持久化生命周期,容器在应用停止后继续运行</description></item>
/// </list>
/// </remarks>
static IResourceBuilder<ContainerResource> AddConsul(IDistributedApplicationBuilder builder)
{
    var consul = builder.AddContainer("consul", "hashicorp/consul", "1.22")
        .WithEnvironment("CONSUL_BIND_INTERFACE", "eth0")
        .WithArgs("agent", "-server", "-bootstrap-expect=1", "-ui", "-client=0.0.0.0", "-data-dir=/consul/data")
        .WithHttpEndpoint(port: 8500, targetPort: 8500, name: "http")
        .WithEndpoint(port: 8600, targetPort: 8600, name: "dns", scheme: "udp")
        .WithBindMount("consul-data", "/consul/data")
        .WithLifetime(ContainerLifetime.Persistent);

    //consul.Resource.Annotations.Add(new ContainerNameAnnotation() { Name = "consul" });
    return consul;
}





/// <summary>
/// 添加 Nacos 服务注册与配置中心容器到 Aspire 应用程序
/// </summary>
/// <param name="builder">分布式应用程序构建器</param>
/// <returns>配置好的 Nacos 容器资源构建器</returns>
/// <remarks>
/// 此方法配置 Nacos 服务器容器,包括:
/// <list type="bullet">
/// <item><description>使用 standalone 模式运行</description></item>
/// <item><description>启用身份验证功能</description></item>
/// <item><description>暴露 HTTP (8848) 和 gRPC (9848) 端点</description></item>
/// <item><description>持久化数据到 nacos-data 卷</description></item>
/// <item><description>使用持久化生命周期,容器在应用停止后继续运行</description></item>
/// </list>
/// 认证配置优先从环境变量读取,如果未设置则使用默认值
/// </remarks>
static IResourceBuilder<ContainerResource> AddNacos(IDistributedApplicationBuilder builder)
{
    return builder.AddContainer("nacos", "nacos/nacos-server", "v2.5.1")
        .WithEnvironment("MODE", "standalone")
        .WithEnvironment("NACOS_AUTH_ENABLE", "true")
        .WithEnvironment("NACOS_AUTH_TOKEN", Environment.GetEnvironmentVariable("NACOS_AUTH_TOKEN") ?? "default-token")
        .WithEnvironment("NACOS_AUTH_IDENTITY_KEY", Environment.GetEnvironmentVariable("NACOS_AUTH_IDENTITY_KEY") ?? "default-key")
        .WithEnvironment("NACOS_AUTH_IDENTITY_VALUE", Environment.GetEnvironmentVariable("NACOS_AUTH_IDENTITY_VALUE") ?? "default-value")
        .WithHttpEndpoint(port: 8848, targetPort: 8848, name: "http")
        .WithEndpoint(port: 9848, targetPort: 9848, name: "grpc")
        .WithBindMount("nacos-data", "/nacos/data/derby-data")
        .WithLifetime(ContainerLifetime.Persistent);
}
