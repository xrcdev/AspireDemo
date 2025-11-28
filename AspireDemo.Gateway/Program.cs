using Consul;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Yarp.ReverseProxy.Configuration;

namespace AspireDemo.Gateway;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();

        builder.Services.AddReverseProxy()
            //.LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
            .LoadFromMemory(
                new List<Yarp.ReverseProxy.Configuration.RouteConfig>(),
                new List<Yarp.ReverseProxy.Configuration.ClusterConfig>()
        );
        //.ConfigureHttpClient((context, handler) =>
        //{
        //    // 忽略 SSL 证书错误（仅用于开发环境）
        //    handler.SslOptions.RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => true;
        //    //HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        //});

        // 注册 Consul 客户端
        builder.Services.AddSingleton<IConsulClient>(sp =>
        {
            var consulAddress = Environment.GetEnvironmentVariable("CONSUL_ADDRESS") ??
                sp.GetRequiredService<IConfiguration>()["Consul:Address"];
            return new ConsulClient(config =>
            {
                config.Address = new Uri(consulAddress);
            });
        });

        builder.Services.AddHostedService<ProxyConfigHostedService>();

        var app = builder.Build();

        app.UseRouting();
        app.MapGet("/", () => "Welcome to AspireDemo.Gateway!");

        //if (app.Environment.IsDevelopment())
        //{
        // 添加 YARP 配置查看端点
        app.MapGet("/api/yarpconfig", (InMemoryConfigProvider configProvider) =>
        {
            var config = configProvider.GetConfig();

            return Results.Json(config, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
        }).AllowAnonymous();//演示用
                            //}

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
