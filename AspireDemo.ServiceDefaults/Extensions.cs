using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ServiceDiscovery;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

// Adds common Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(tracing =>
                        // Exclude health check requests from tracing
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
                    )
                    // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                    //.AddGrpcClientInstrumentation()
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        // 1) OTLP Exporter - 外部 Collector 导出
        var enableOtlp = builder.Configuration["OpenTelemetry:Exporters:Otlp:Enabled"];
        var useOtlpExporter = string.IsNullOrWhiteSpace(enableOtlp) || bool.Parse(enableOtlp);
        
        if (useOtlpExporter)
        {
            var externalEndpoint = builder.Configuration["OTEL_PERSISTENCE_EXPORTER_ENDPOINT"];
            if (string.IsNullOrWhiteSpace(externalEndpoint)) externalEndpoint = "http://localhost:4318";

            if (!string.IsNullOrWhiteSpace(externalEndpoint))
            {
                var endpointUri = new Uri(externalEndpoint);

                builder.Services.Configure<OpenTelemetryLoggerOptions>(logging => logging.AddOtlpExporter(opts =>
                {
                    opts.Protocol = OtlpExportProtocol.HttpProtobuf;
                    opts.Endpoint = endpointUri;
                }));
                builder.Services.Configure<TracerProviderBuilder>(tracing => tracing.AddOtlpExporter(opts =>
                {
                    opts.Protocol = OtlpExportProtocol.HttpProtobuf;
                    opts.Endpoint = endpointUri;
                }));
                builder.Services.Configure<MeterProviderBuilder>(metrics => metrics.AddOtlpExporter(opts =>
                {
                    opts.Protocol = OtlpExportProtocol.HttpProtobuf;
                    opts.Endpoint = endpointUri;
                }));
            }
        }

        // 2) Console Exporter - 控制台输出（调试用）
        var enableConsole = builder.Configuration["OpenTelemetry:Exporters:Console:Enabled"];
        var useConsoleExporter = !string.IsNullOrWhiteSpace(enableConsole) && bool.Parse(enableConsole);
        
        if (useConsoleExporter)
        {
            builder.Services.Configure<OpenTelemetryLoggerOptions>(logging => logging.AddConsoleExporter());
            builder.Services.Configure<TracerProviderBuilder>(tracing => tracing.AddConsoleExporter());
            builder.Services.Configure<MeterProviderBuilder>(metrics => metrics.AddConsoleExporter());
        }

        // 3) InMemory Exporter - 内存存储（测试用）
        var enableInMemory = builder.Configuration["OpenTelemetry:Exporters:InMemory:Enabled"];
        var useInMemoryExporter = !string.IsNullOrWhiteSpace(enableInMemory) && bool.Parse(enableInMemory);
        
        if (useInMemoryExporter)
        {
            builder.Services.Configure<OpenTelemetryLoggerOptions>(logging => logging.AddInMemoryExporter(builder.Services.BuildServiceProvider().GetService<ICollection<LogRecord>>()));
            builder.Services.Configure<TracerProviderBuilder>(tracing => tracing.AddInMemoryExporter(builder.Services.BuildServiceProvider().GetService<ICollection<Activity>>()));
            builder.Services.Configure<MeterProviderBuilder>(metrics => metrics.AddInMemoryExporter(builder.Services.BuildServiceProvider().GetService<ICollection<Metric>>()));
        }

        // 4) Prometheus HttpListener Exporter - Prometheus 拉取模式
        var enablePrometheus = builder.Configuration["OpenTelemetry:Exporters:Prometheus:Enabled"];
        var usePrometheusExporter = !string.IsNullOrWhiteSpace(enablePrometheus) && bool.Parse(enablePrometheus);
        
        if (usePrometheusExporter)
        {
            var prometheusPort = builder.Configuration["OpenTelemetry:Exporters:Prometheus:Port"];
            var port = string.IsNullOrWhiteSpace(prometheusPort) ? 9464 : int.Parse(prometheusPort);
            
            builder.Services.Configure<MeterProviderBuilder>(metrics => metrics.AddPrometheusHttpListener(opts =>
            {
                opts.UriPrefixes = [$"http://localhost:{port}/"];
            }));
        }

        //Uncomment the following lines to enable the Azure Monitor exporter(requires the Azure.Monitor.OpenTelemetry.AspNetCore package)
        //if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        //{
        //    builder.Services.AddOpenTelemetry()
        //       //.UseAzureMonitor();
        //}

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Adding health checks endpoints to applications in non-development environments has security implications.
        // See https://aka.ms/dotnet/aspire/healthchecks for details before enabling these endpoints in non-development environments.
        if (app.Environment.IsDevelopment())
        {
            // All health checks must pass for app to be considered ready to accept traffic after starting
            app.MapHealthChecks(HealthEndpointPath);

            // Only health checks tagged with the "live" tag must pass for app to be considered alive
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            });
        }

        return app;
    }
}
