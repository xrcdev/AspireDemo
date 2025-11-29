using AspireDemo.ServiceDefaults.Consul;
using Google.Protobuf.WellKnownTypes;
using System.Net;
using System.Net.Sockets;

var builder = WebApplication.CreateBuilder(args);

// 智能端口配置
ConfigureSmartPort(builder);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

/// <summary>
/// 智能端口配置：
/// 1. 优先使用环境变量指定的端口 (ASPNETCORE_URLS, PORT, ASPNETCORE_HTTP_PORTS)
/// 2. 如果在 Docker 环境中且没有指定端口，使用默认端口 8080
/// 3. 如果在非容器环境且没有指定端口，自动查找可用端口
/// </summary>
static void ConfigureSmartPort(WebApplicationBuilder builder)
{
    // 检查是否已经通过环境变量或命令行指定了端口
    var aspnetCoreUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
    var commandLineUrls = builder.Configuration["urls"]; // <-- 从命令行参数获取 --urls 的
    //var portEnv = Environment.GetEnvironmentVariable("PORT");
    //var httpPorts = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS");
    //var httpsPorts = Environment.GetEnvironmentVariable("ASPNETCORE_HTTPS_PORTS");

    bool isPortSpecified = !string.IsNullOrEmpty(aspnetCoreUrls)
        || !string.IsNullOrEmpty(commandLineUrls) // <-- 检查命令行参数是否提供了 urls
                                                  //|| !string.IsNullOrEmpty(portEnv)
                                                  //|| !string.IsNullOrEmpty(httpPorts)
                                                  //|| !string.IsNullOrEmpty(httpsPorts)
                          ;

    // 检测是否在 Docker/容器环境中
    bool isDockerEnvironment = IsRunningInDocker();

    Console.WriteLine($"[SmartPort] Docker Environment: {isDockerEnvironment}");
    Console.WriteLine($"[SmartPort] Port Specified: {isPortSpecified}");

    if (isPortSpecified)
    {
        Console.WriteLine($"[SmartPort] Using specified port configuration");
        Console.WriteLine($"[SmartPort] ASPNETCORE_URLS: {aspnetCoreUrls ?? "not set"}");
        Console.WriteLine($"[SmartPort] Command line --urls: {commandLineUrls ?? "not set"}"); // <-- 打印获取到的值
        //Console.WriteLine($"[SmartPort] PORT: {portEnv ?? "not set"}");
        //Console.WriteLine($"[SmartPort] ASPNETCORE_HTTP_PORTS: {httpPorts ?? "not set"}");
        return; // 使用已指定的端口配置
    }

    if (isDockerEnvironment)
    {
        // Docker 环境下，如果没有指定端口，使用默认端口 8080
        // 因为 Docker 通常通过 -p 参数映射端口，容器内使用固定端口更简单
        int defaultDockerPort = 8080;
        var url = $"http://*:{defaultDockerPort}";
        builder.WebHost.UseUrls(url);
        Console.WriteLine($"[SmartPort] Docker mode - Using default port: {url}");
    }
    else
    {
        // 非容器环境，自动查找可用端口
        int availablePort = GetAvailablePort();
        var url = $"http://localhost:{availablePort}";
        builder.WebHost.UseUrls(url);
        Console.WriteLine($"[SmartPort] Standalone mode - Auto-selected available port: {url}");
    }
}

/// <summary>
/// 检测是否运行在 Docker/容器环境中
/// </summary>
static bool IsRunningInDocker()
{
    // 方法1: 检查 DOTNET_RUNNING_IN_CONTAINER 环境变量 (官方 .NET Docker 镜像设置)
    var dotnetInContainer = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
    if (!string.IsNullOrEmpty(dotnetInContainer) && dotnetInContainer.Equals("true", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    // 方法2: 检查 /.dockerenv 文件 (Linux Docker 环境)
    if (File.Exists("/.dockerenv"))
    {
        return true;
    }

    // 方法3: 检查 /proc/1/cgroup 是否包含 docker 或 kubepods (Linux)
    try
    {
        if (File.Exists("/proc/1/cgroup"))
        {
            var cgroupContent = File.ReadAllText("/proc/1/cgroup");
            if (cgroupContent.Contains("docker") || cgroupContent.Contains("kubepods") || cgroupContent.Contains("containerd"))
            {
                return true;
            }
        }
    }
    catch
    {
        // 忽略读取错误
    }

    // 方法4: 检查常见的容器环境变量
    var containerEnvVars = new[] { "KUBERNETES_SERVICE_HOST", "CONTAINER_NAME" };
    foreach (var envVar in containerEnvVars)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar)))
        {
            return true;
        }
    }

    return false;
}

/// <summary>
/// 获取系统可用的端口号
/// </summary>
static int GetAvailablePort()
{
    // 使用 TcpListener 绑定到端口 0，让系统自动分配可用端口
    var listener = new TcpListener(IPAddress.Loopback, 0);
    try
    {
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return port;
    }
    finally
    {
        listener.Stop();
    }
}


// 添加 Consul 服务注册
builder.AddConsulServiceRegistration();

// Add services to the container.
builder.Services.AddProblemDetails();

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
}

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

string[] summaries = ["Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"];
app.MapGet("/weather/api/weatherforecast", () =>
{
    var formUrl = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "Unknown";

    var forecast = Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)],
            formUrl
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

app.MapDefaultEndpoints();

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary, string FromUrl)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
