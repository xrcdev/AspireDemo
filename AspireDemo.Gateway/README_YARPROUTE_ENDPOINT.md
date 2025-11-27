# YARP 配置查看端点

## 端点信息

**URL**: `/yarproute`  
**方法**: GET  
**认证**: 无需认证 (AllowAnonymous)

## 功能说明

此端点用于实时查看 YARP 网关当前的路由和集群配置。配置信息从 Consul 动态获取并每 10 秒刷新一次(默认配置)。

## 返回格式

返回 JSON 格式,包含以下信息:

```json
{
  "routes": [
    {
      "routeId": "route-apiservice",
      "clusterId": "cluster-apiservice",
      "order": null,
      "match": {
        "path": "/api/{**catch-all}",
        "hosts": null,
        "methods": null,
        "headers": null,
        "queryParameters": null
      },
      "metadata": {
        "protocol": "http"
      },
      "transforms": [
        {
          "PathRemovePrefix": "/api/apiservice"
        }
      ]
    }
  ],
  "clusters": [
    {
      "clusterId": "cluster-apiservice",
      "loadBalancingPolicy": "RoundRobin",
      "sessionAffinity": null,
      "metadata": {
        "serviceName": "apiservice"
      },
      "destinations": {
        "apiservice-10.10.11.130-7523-abc123": {
          "address": "https://10.10.11.130:7523",
          "health": null,
          "metadata": {
            "weight": "1",
            "protocol": "http"
          }
        }
      },
      "healthCheck": {
        "active": {
          "enabled": true,
          "interval": "00:00:30",
          "timeout": "00:00:10",
          "policy": null,
          "path": "/health"
        },
        "passive": {
          "enabled": true,
          "policy": "TransportFailureRateHealthPolicy",
          "reactivationPeriod": "00:01:00"
        }
      }
    }
  ],
  "timestamp": "2025-01-26T10:30:45.123Z"
}
```

## 使用示例

### 浏览器访问

直接在浏览器中访问:
```
http://localhost:5106/yarproute
https://localhost:7145/yarproute
```

### PowerShell

```powershell
# 查看配置
Invoke-RestMethod -Uri "http://localhost:5106/yarproute" -Method Get

# 格式化输出
(Invoke-RestMethod -Uri "http://localhost:5106/yarproute").routes | Format-Table routeId, clusterId, @{Name='Path';Expression={$_.match.path}}
```

### curl

```bash
# 查看配置
curl http://localhost:5106/yarproute

# 使用 jq 格式化
curl http://localhost:5106/yarproute | jq .
```

### C# HttpClient

```csharp
using var client = new HttpClient();
var response = await client.GetStringAsync("http://localhost:5106/yarproute");
Console.WriteLine(response);
```

## 配置信息说明

### Routes (路由)

- **routeId**: 路由唯一标识
- **clusterId**: 关联的集群 ID
- **order**: 路由优先级 (数字越小优先级越高)
- **match.path**: 匹配的路径模式
- **transforms**: 路径转换规则
- **metadata**: 路由元数据

### Clusters (集群)

- **clusterId**: 集群唯一标识
- **loadBalancingPolicy**: 负载均衡策略
  - `RoundRobin`: 轮询
  - `PowerOfTwoChoices`: 两次选择法(适用于权重不同的实例)
  - `Random`: 随机
  - `FirstAlphabetical`: 按字母顺序选择第一个
  - `LeastRequests`: 最少请求

- **destinations**: 目标服务实例列表
  - **address**: 服务地址
  - **health**: 自定义健康检查地址
  - **metadata**: 实例元数据(权重、协议等)

- **healthCheck**: 健康检查配置
  - **active**: 主动健康检查(定期轮询)
  - **passive**: 被动健康检查(基于请求失败率)

## 调试用途

### 1. 验证服务注册

查看从 Consul 获取的服务是否正确注册:

```powershell
$config = Invoke-RestMethod -Uri "http://localhost:5106/yarproute"
$config.clusters | ForEach-Object {
    Write-Host "Cluster: $($_.clusterId)"
    $_.destinations.PSObject.Properties | ForEach-Object {
        Write-Host "  - $($_.Name): $($_.Value.address)"
    }
}
```

### 2. 检查路由配置

验证路由路径是否正确:

```powershell
$config = Invoke-RestMethod -Uri "http://localhost:5106/yarproute"
$config.routes | Select-Object routeId, @{Name='Path';Expression={$_.match.path}}, @{Name='Cluster';Expression={$_.clusterId}}
```

### 3. 查看负载均衡策略

```powershell
$config = Invoke-RestMethod -Uri "http://localhost:5106/yarproute"
$config.clusters | Select-Object clusterId, loadBalancingPolicy, @{Name='DestinationCount';Expression={$_.destinations.PSObject.Properties.Count}}
```

### 4. 监控配置变化

定期检查配置更新:

```powershell
while ($true) {
    $config = Invoke-RestMethod -Uri "http://localhost:5106/yarproute"
    Clear-Host
    Write-Host "Last Update: $($config.timestamp)"
    Write-Host "Routes: $($config.routes.Count)"
    Write-Host "Clusters: $($config.clusters.Count)"
    
    $config.clusters | ForEach-Object {
        $destCount = $_.destinations.PSObject.Properties.Count
        Write-Host "$($_.clusterId): $destCount destinations"
    }
    
    Start-Sleep -Seconds 5
}
```

## 实现原理

1. **InMemoryConfigProvider**: 实现了 `IProxyConfigProvider` 接口,存储当前的 YARP 配置
2. **ProxyConfigHostedService**: 后台服务,定期从 Consul 获取服务信息并更新配置
3. **配置更新流程**:
   - 从 Consul 获取所有服务及其实例
   - 检测配置是否变化
   - 如有变化,生成新的路由和集群配置
   - 调用 `InMemoryConfigProvider.Update()` 更新配置
   - YARP 自动应用新配置

## 注意事项

1. **性能**: 此端点仅用于调试和监控,不建议在生产环境中频繁调用
2. **安全**: 当前配置为 `AllowAnonymous`,生产环境应添加认证授权
3. **实时性**: 配置更新间隔由 `Consul:RefreshIntervalSeconds` 配置项控制(默认 10 秒)

## 配置更新间隔

修改 `appsettings.json`:

```json
{
  "Consul": {
    "Address": "http://localhost:8500",
    "RefreshIntervalSeconds": 5
  }
}
```

## 相关端点

- `/health`: 网关健康检查
- `/alive`: 网关存活检查
- `/`: 欢迎页面
