# Consul 服务注册 - Docker 环境配置指南

## 问题场景

当 **Consul 运行在 Docker 容器**中,而 **应用服务运行在主机**(如 Windows)上时,会遇到网络访问问题:

- Consul 容器无法通过 `localhost` 或 `127.0.0.1` 访问主机上的服务
- 服务只监听 `localhost` 时,外部网络无法访问

## 解决方案

### 1. 修改应用监听地址 (launchSettings.json)

将 `applicationUrl` 从 `localhost` 改为 `0.0.0.0`:

```json
{
  "profiles": {
    "https": {
      "applicationUrl": "https://0.0.0.0:7523;http://0.0.0.0:5381"
    }
  }
}
```

**为什么使用 `0.0.0.0`?**
- `localhost`/`127.0.0.1`: 只监听回环接口,只能本机访问
- `0.0.0.0`: 监听所有网络接口,允许局域网和容器访问

### 2. 配置首选网络段 (Program.cs)

使用 `PreferredNetworks` 指定正确的网络段:

```csharp
builder.AddConsulServiceRegistration(options =>
{
    options.ServiceName = "apiservice";
    options.PreferredNetworks = "10.10";  // 匹配你的主机 IP 10.10.11.130
});
```

### 3. Consul Docker 配置

#### Windows/Mac Docker Desktop

Consul 容器可以通过 `host.docker.internal` 访问主机:

```bash
docker run -d \
  --name consul \
  -p 8500:8500 \
  --add-host=host.docker.internal:host-gateway \
  hashicorp/consul:1.22 agent \
  -server -ui -bootstrap-expect=1 -client=0.0.0.0
```

然后在代码中配置:

```csharp
// 方式1: 使用环境变量
Environment.SetEnvironmentVariable("DOCKER_HOST_IP", "host.docker.internal");

// 方式2: 在 appsettings.json 中配置
{
  "Consul": {
    "ServiceAddress": "host.docker.internal"
  }
}
```

#### Linux Docker

Linux 需要手动指定主机网关:

```bash
# 方式1: 使用 host network 模式 (推荐用于开发)
docker run -d \
  --name consul \
  --network host \
  hashicorp/consul:1.22 agent \
  -server -ui -bootstrap-expect=1 -client=0.0.0.0

# 方式2: 使用 bridge 网络 + host-gateway
docker run -d \
  --name consul \
  -p 8500:8500 \
  --add-host=host.docker.internal:172.17.0.1 \
  hashicorp/consul:1.22 agent \
  -server -ui -bootstrap-expect=1 -client=0.0.0.0
```

## 完整配置示例

### appsettings.json

```json
{
  "Consul": {
    "ConsulAddress": "http://localhost:8500",
    "ServiceName": "apiservice",
    "PreferredNetworks": "10.10",
    "HealthCheckPath": "/health",
    "HealthCheckIntervalSeconds": 10,
    "HealthCheckTimeoutSeconds": 5,
    "HttpScheme": "https"
  }
}
```

### Program.cs

```csharp
builder.AddConsulServiceRegistration(options =>
{
    options.ServiceName = "apiservice";
    options.PreferredNetworks = "10.10";  // 优先使用 10.10.x.x 网段的 IP
    options.PathPrefix = "/api";
    options.Protocol = ServiceProtocolTypes.Http;
});
```

### launchSettings.json

```json
{
  "profiles": {
    "https": {
      "applicationUrl": "https://0.0.0.0:7523;http://0.0.0.0:5381"
    }
  }
}
```

## 验证步骤

1. **启动 Consul**:
   ```bash
   docker run -d --name consul -p 8500:8500 hashicorp/consul:1.22 agent -server -ui -bootstrap-expect=1 -client=0.0.0.0
   ```

2. **启动应用服务**:
   ```bash
   dotnet run --project AspireDemo.ApiService
   ```

3. **检查服务注册**:
   - 打开浏览器: http://localhost:8500/ui
   - 查看服务列表,确认 `apiservice` 已注册
   - 检查健康检查状态是否为绿色

4. **测试连接**:
   ```bash
   # 从主机测试
   curl https://10.10.11.130:7523/health -k
   
   # 从 Consul 容器内测试
   docker exec consul curl https://10.10.11.130:7523/health -k
   ```

## 常见问题

### Q1: 服务注册了但健康检查失败

**原因**: Consul 容器无法访问服务的健康检查端点

**解决**:
1. 确认 `launchSettings.json` 使用 `0.0.0.0`
2. 检查防火墙是否允许端口访问
3. 确认 `PreferredNetworks` 配置正确

### Q2: HTTPS 证书警告

**原因**: 开发环境使用自签名证书

**解决**: 代码中已设置 `TLSSkipVerify = true`,开发环境无需额外配置

### Q3: 找不到合适的网络接口

**原因**: 多网卡环境没有正确配置 `PreferredNetworks`

**解决**:
```bash
# 查看所有网络接口
ipconfig (Windows) 或 ifconfig (Linux/Mac)

# 找到实际使用的 IP,如 10.10.11.130
# 配置 PreferredNetworks = "10.10"
```

## 网络模式对比

| 模式 | Consul 位置 | 服务位置 | 配置要点 |
|------|------------|---------|---------|
| **主机-容器** | Docker | 主机 | 使用 `0.0.0.0` + `PreferredNetworks` |
| 容器-容器 | Docker | Docker | 使用 Docker 网络名称 |
| 主机-主机 | 主机 | 主机 | 默认配置即可 |

## 生产环境建议

1. **使用实际 IP 而非 `host.docker.internal`**
   ```csharp
   options.ServiceAddress = "10.10.11.130";  // 明确指定
   ```

2. **配置正确的 Scheme**
   ```csharp
   options.HttpScheme = "https";  // 生产环境使用 HTTPS
   ```

3. **调整健康检查参数**
   ```csharp
   options.HealthCheckIntervalSeconds = 30;
   options.HealthCheckTimeoutSeconds = 10;
   ```

4. **使用专用网络段**
   ```csharp
   options.PreferredNetworks = "192.168.1";  // 生产内网段
   ```
