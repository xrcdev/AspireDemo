using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Google.Protobuf.WellKnownTypes;

// 创建 Aspire 分布式应用程序构建器
// 这是 Aspire 应用的入口点，用于编排所有服务和资源
var builder = DistributedApplication.CreateBuilder(args);

// ========== 添加 Redis 缓存服务 ==========
// Redis 用于分布式缓存和会话存储
var cache = builder.AddRedis("cache")
    .WithImageTag("8.2")  // 使用 Redis 8.2 版本
    .PublishAsConnectionString(); // 在发布清单中以连接字符串形式导出，便于部署到其他环境

// ========== 添加 Consul 服务发现与配置中心 ==========
// Consul 提供服务注册、发现和分布式配置功能
var consul = AddConsul(builder)
    .PublishAsContainer();  // 在发布清单中以容器形式导出

// ========== 添加 API 服务 ==========
// ApiService 是后端 API 服务，提供天气数据等接口
var apiService = builder.AddProject<Projects.AspireDemo_ApiService>("apiservice")
    .WithHttpHealthCheck("/health")  // 配置健康检查端点，用于监控服务状态
    //.WithReference(consul)  // 引用 Consul 服务（已注释，改用环境变量传递）
    //.WithAnnotation(new ResourceRelationshipAnnotation(consul.Resource, "Reference"))
    .WaitFor(consul)  // 确保 Consul 启动后再启动此服务
    .WithEnvironment("CONSUL_ADDRESS", consul.GetEndpoint("http"))  // 传递 Consul 的 HTTP 端点地址
    .WithReplicas(2)  // 启动 2 个实例，实现负载均衡和高可用
    .PublishAsDockerFile(); // 在发布清单中生成 Dockerfile，便于容器化部署

// ========== 添加 API 网关服务 ==========
// Gateway 作为 API 网关，统一路由和转发请求
var gateway = builder.AddProject<Projects.AspireDemo_Gateway>("gateway")
    .WithEnvironment("CONSUL_ADDRESS", consul.GetEndpoint("http"))  // 传递 Consul 地址用于服务发现
    .WaitFor(consul);  // 等待 Consul 启动

// ========== 添加 Web 前端服务 ==========
// Web 是基于 Blazor 的前端应用
var webfrontend = builder.AddProject<Projects.AspireDemo_Web>("webfrontend")
    .WithExternalHttpEndpoints()  // 允许外部访问，通常用于对外提供服务的前端
    .WithHttpHealthCheck("/health")  // 配置健康检查端点
    .WithReference(cache)  // 引用 Redis 缓存服务，自动注入连接字符串
    .WaitFor(cache)  // 等待 Redis 启动
    .WithReference(apiService)  // 引用 API 服务，用于服务间通信
    .WaitFor(apiService)  // 等待 API 服务启动
    .WithReference(gateway)  // 引用网关服务
    .WaitFor(gateway)  // 等待网关启动
    .PublishAsDockerFile();  // 生成 Dockerfile 用于容器化部署

// ========== 可选：Scalar API 文档 ==========
// Scalar 是一个现代化的 API 文档工具（已注释）
//var scalar= builder.AddScalarApiReference();
//scalar.WithReference(apiService);

// ========== 执行上下文判断 ==========
// IsRunMode: 调试模式（在 Visual Studio 中 F5 运行）
// 如果不是 RunMode，则是发布清单模式（生成部署配置）
if (builder.ExecutionContext.IsRunMode)
{
    // 调试运行模式
    // 可以在这里添加仅在开发调试时需要的配置
}
else
{
    // 发布清单生成模式
    // 可以在这里添加仅在生成部署清单时需要的配置
}

// 构建并运行应用程序
builder.Build().Run();



/// <summary>
/// 添加 Consul 服务发现与配置中心容器到 Aspire 应用程序
/// </summary>
/// <param name="builder">分布式应用程序构建器</param>
/// <returns>配置好的 Consul 容器资源构建器</returns>
/// <remarks>
/// 此方法配置 Consul 服务器容器，包括：
/// <list type="bullet">
/// <item><description>使用开发模式运行 (dev mode)</description></item>
/// <item><description>允许任何客户端连接</description></item>
/// <item><description>暴露 HTTP (8500) 和 DNS (8600) 端点</description></item>
/// <item><description>持久化数据到 consul-data 卷</description></item>
/// <item><description>使用持久化生命周期，容器在应用停止后继续运行</description></item>
/// </list>
/// 
/// <para><strong>Consul 端口说明：</strong></para>
/// <list type="bullet">
/// <item><description>8500: HTTP API 和 Web UI 端口</description></item>
/// <item><description>8600: DNS 查询端口（UDP）</description></item>
/// </list>
/// 
/// <para><strong>启动参数说明：</strong></para>
/// <list type="bullet">
/// <item><description>agent: 启动 Consul 代理</description></item>
/// <item><description>-server: 以服务器模式运行</description></item>
/// <item><description>-bootstrap-expect=1: 期望 1 个服务器节点（单节点集群）</description></item>
/// <item><description>-ui: 启用 Web UI 界面</description></item>
/// <item><description>-client=0.0.0.0: 允许所有 IP 访问客户端接口</description></item>
/// <item><description>-data-dir=/consul/data: 指定数据存储目录</description></item>
/// </list>
/// </remarks>
static IResourceBuilder<ContainerResource> AddConsul(IDistributedApplicationBuilder builder)
{
    var consul = builder.AddContainer("consul", "hashicorp/consul", "1.22")
        // 绑定网络接口到 eth0（容器内的主网卡）
        .WithEnvironment("CONSUL_BIND_INTERFACE", "eth0")
        // 配置 Consul 启动参数
        .WithArgs("agent",                  // 启动 Consul 代理
                  "-server",                // 以服务器模式运行
                  "-bootstrap-expect=1",    // 单节点集群配置
                  "-ui",                    // 启用 Web UI
                  "-client=0.0.0.0",        // 允许所有客户端连接
                  "-data-dir=/consul/data") // 数据存储目录
        // 暴露 HTTP 端点（Web UI 和 API）
        .WithHttpEndpoint(port: 8500, targetPort: 8500, name: "http")
        // 暴露 DNS 端点（UDP 协议）
        .WithEndpoint(port: 8600, targetPort: 8600, name: "dns", scheme: "udp")
        // 将主机的 consul-data 目录挂载到容器的 /consul/data，实现数据持久化
        .WithBindMount("consul-data", "/consul/data")
        // 使用持久化生命周期，即使 Aspire 应用停止，Consul 容器也会继续运行
        // 这样可以保留服务注册信息和配置数据
        .WithLifetime(ContainerLifetime.Persistent);

    // 可选：为容器设置自定义名称（已注释）
    //consul.Resource.Annotations.Add(new ContainerNameAnnotation() { Name = "consul" });
    return consul;
}

/// <summary>
/// 添加 Nacos 服务注册与配置中心容器到 Aspire 应用程序
/// </summary>
/// <param name="builder">分布式应用程序构建器</param>
/// <returns>配置好的 Nacos 容器资源构建器</returns>
/// <remarks>
/// 此方法配置 Nacos 服务器容器，包括：
/// <list type="bullet">
/// <item><description>使用 standalone 模式运行（单机模式）</description></item>
/// <item><description>启用身份验证功能，提高安全性</description></item>
/// <item><description>暴露 HTTP (8848) 和 gRPC (9848) 端点</description></item>
/// <item><description>持久化数据到 nacos-data 卷</description></item>
/// <item><description>使用持久化生命周期，容器在应用停止后继续运行</description></item>
/// </list>
/// 
/// <para><strong>Nacos 端口说明：</strong></para>
/// <list type="bullet">
/// <item><description>8848: HTTP API 和 Web 控制台端口</description></item>
/// <item><description>9848: gRPC 端口（用于客户端与服务器的高性能通信）</description></item>
/// </list>
/// 
/// <para><strong>认证配置：</strong></para>
/// <para>
/// 认证配置优先从环境变量读取，如果未设置则使用默认值。
/// 建议在生产环境中通过 launchSettings.json 或环境变量设置安全的认证信息。
/// </para>
/// <list type="bullet">
/// <item><description>NACOS_AUTH_TOKEN: 用于 JWT 签名的密钥（必须 >= 32 字符）</description></item>
/// <item><description>NACOS_AUTH_IDENTITY_KEY: 身份验证密钥</description></item>
/// <item><description>NACOS_AUTH_IDENTITY_VALUE: 身份验证值</description></item>
/// </list>
/// 
/// <para><strong>访问方式：</strong></para>
/// <para>
/// 容器启动后，可通过 http://localhost:8848/nacos 访问 Web 控制台。
/// 默认用户名/密码: nacos/nacos
/// </para>
/// </remarks>
static IResourceBuilder<ContainerResource> AddNacos(IDistributedApplicationBuilder builder)
{
    return builder.AddContainer("nacos", "nacos/nacos-server", "v2.5.1")
        // 设置为单机模式（适合开发和测试环境）
        .WithEnvironment("MODE", "standalone")
        // 启用身份验证，提高安全性
        .WithEnvironment("NACOS_AUTH_ENABLE", "true")
        // JWT Token 密钥，用于签名（长度必须 >= 32 字符）
        // 优先从环境变量读取，未设置则使用默认值
        .WithEnvironment("NACOS_AUTH_TOKEN", Environment.GetEnvironmentVariable("NACOS_AUTH_TOKEN") ?? "default-token")
        // 身份验证密钥，用于服务端与客户端的身份验证
        .WithEnvironment("NACOS_AUTH_IDENTITY_KEY", Environment.GetEnvironmentVariable("NACOS_AUTH_IDENTITY_KEY") ?? "default-key")
        // 身份验证值，与 IDENTITY_KEY 配合使用
        .WithEnvironment("NACOS_AUTH_IDENTITY_VALUE", Environment.GetEnvironmentVariable("NACOS_AUTH_IDENTITY_VALUE") ?? "default-value")
        // 暴露 HTTP 端点（Web UI 和 HTTP API）
        .WithHttpEndpoint(port: 8848, targetPort: 8848, name: "http")
        // 暴露 gRPC 端口（用于高性能的客户端通信）
        .WithEndpoint(port: 9848, targetPort: 9848, name: "grpc")
        // 挂载数据卷，持久化 Nacos 的配置和服务注册信息
        // 使用 Derby 内嵌数据库存储数据
        .WithBindMount("nacos-data", "/nacos/data/derby-data")
        // 持久化生命周期：容器在应用停止后继续运行
        // 这样可以保留服务注册信息和配置数据，下次启动时无需重新注册
        .WithLifetime(ContainerLifetime.Persistent);
}
