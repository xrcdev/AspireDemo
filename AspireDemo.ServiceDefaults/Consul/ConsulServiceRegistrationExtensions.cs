using Consul;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AspireDemo.ServiceDefaults.Consul;

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
        // 从配置中读取
        var options = builder.Configuration.GetSection("Consul")
            .Get<ConsulServiceRegistrationOptions>() ?? new();
        options.ConsulAddress = Environment.GetEnvironmentVariable("CONSUL_ADDRESS") ?? options.ConsulAddress;
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
