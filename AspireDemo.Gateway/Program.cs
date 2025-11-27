using Consul;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Configuration;
using System.Collections.Generic; // ���������ռ���֧�� List<T>

namespace AspireDemo.Gateway;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();

        // 注册 Consul 客户端
        builder.Services.AddSingleton<IConsulClient>(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var consulAddress = configuration["Consul:Address"] ?? "http://localhost:8500";
            return new ConsulClient(config =>
            {
                config.Address = new Uri(consulAddress);
            });
        });

        builder.Services.AddReverseProxy().LoadFromMemory(
            new List<Yarp.ReverseProxy.Configuration.RouteConfig>(),
            new List<Yarp.ReverseProxy.Configuration.ClusterConfig>()
        );
        //builder.Services.AddAuthorization();
        //.LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
        builder.Services.AddHostedService<ProxyConfigHostedService>();
        
        var app = builder.Build();
        app.UseRouting();
        app.MapGet("/", () => "Welcome to AspireDemo.Gateway!"); // inutile, mais c'est pour la démo :)
        
        // 添加 YARP 配置查看端点
        app.MapGet("/api/yarpconfig", (InMemoryConfigProvider configProvider) =>
        {
            var config = configProvider.GetConfig();
            
            return Results.Json(config, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
        }).AllowAnonymous();
        
        app.MapHealthChecks("/health").AllowAnonymous();
        app.MapHealthChecks("/alive", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        }).AllowAnonymous();
        //app.MapReverseProxy();
        app.MapReverseProxy(proxyPipeline =>
        {
            // 在此添加管道中间件
            //proxyPipeline.UseXxxMiddleware();
        });

        app.Run();
    }
}
