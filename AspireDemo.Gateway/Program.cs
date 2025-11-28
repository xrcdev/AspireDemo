using Consul;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Yarp.ReverseProxy.Configuration;

namespace AspireDemo.Gateway;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // 注意：AddServiceDefaults 中的 ConfigureHttpClientDefaults 会影响所有 HttpClient
        // 包括 YARP 使用的 HttpClient，需要特别处理
        builder.AddServiceDefaults();

        // 注册 Consul 客户端
        builder.Services.AddSingleton<IConsulClient>(sp =>
        {
            var consulAddress = Environment.GetEnvironmentVariable("CONSUL_ADDRESS") ??
                sp.GetRequiredService<IConfiguration>()["Consul:Address"];
            return new ConsulClient(config =>
            {
                config.Address = new Uri(consulAddress!);
            });
        });

        // 注册 ConsulConfigProvider 作为 IProxyConfigProvider 和 IHostedService
        builder.Services.AddSingleton<ConsulConfigProvider>();
        builder.Services.AddSingleton<IProxyConfigProvider>(sp => sp.GetRequiredService<ConsulConfigProvider>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ConsulConfigProvider>());

        // 使用 ConsulConfigProvider 作为配置源
        builder.Services.AddReverseProxy();
        //.ConfigureHttpClient((context, handler) =>
        //{
        //    // 忽略 SSL 证书错误（仅用于开发环境）
        //    handler.SslOptions.RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => true;
        //});

        var app = builder.Build();

        app.UseRouting();

        app.MapHealthChecks("/health").AllowAnonymous();
        app.MapHealthChecks("/alive", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        }).AllowAnonymous();

        app.MapGet("/", () => "Welcome to AspireDemo.Gateway!");

        //if (app.Environment.IsDevelopment())
        //{
        // 添加 YARP 配置查看端点
        app.MapGet("/api/yarpconfig", (ConsulConfigProvider configProvider) =>
        {
            var config = configProvider.GetConfig();

            return Results.Json(config, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
        }).AllowAnonymous();//演示用

        // 添加路由调试端点
        app.MapGet("/api/routes", (ConsulConfigProvider configProvider) =>
        {
            var config = configProvider.GetConfig();
            var routes = config.Routes.Select(r => new
            {
                r.RouteId,
                r.ClusterId,
                MatchPath = r.Match.Path,
                MatchHosts = r.Match.Hosts,
                MatchMethods = r.Match.Methods
            }).ToList();
            return Results.Json(routes);
        }).AllowAnonymous();
        //app.MapReverseProxy();
        app.MapReverseProxy(proxyPipeline =>
        {
            // 添加调试中间件
            proxyPipeline.Use(async (context, next) =>
            {
                var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
                logger.LogInformation("YARP Processing: {Path}", context.Request.Path);

                try
                {
                    await next();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "YARP Error forwarding request to {Path}", context.Request.Path);
                    throw;
                }

                // 记录代理错误信息
                var proxyError = context.Features.Get<Yarp.ReverseProxy.Forwarder.IForwarderErrorFeature>();
                if (proxyError != null)
                {
                    logger.LogError(proxyError.Exception, "YARP Proxy Error: {Error}", proxyError.Error);
                }
            });
        });

        app.Run();
    }
}
