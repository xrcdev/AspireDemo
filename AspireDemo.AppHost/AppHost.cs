 

var builder = DistributedApplication.CreateBuilder(args);

// 添加 Kubernetes 环境配置
builder.AddKubernetesEnvironment("k8s")
    .WithProperties(k8s =>
    {
        k8s.HelmChartName = "aspiredemo"; // Helm chart 名称
        k8s.DefaultImagePullPolicy = "IfNotPresent"; // 镜像拉取策略
    });
var cache = builder.AddRedis("cache")
 .PublishAsConnectionString(); // 添加发布支持


var apiService = builder.AddProject<Projects.AspireDemo_ApiService>("apiservice")
    .WithHttpHealthCheck("/health").WithReplicas(2).PublishAsDockerFile(); // 生成 Dockerfile;

builder.AddProject<Projects.AspireDemo_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(cache)
    .WaitFor(cache)
    .WithReference(apiService)
    .WaitFor(apiService).PublishAsDockerFile();

//var scalar= builder.AddScalarApiReference();
//scalar.WithReference(apiService);

builder.Build().Run();
