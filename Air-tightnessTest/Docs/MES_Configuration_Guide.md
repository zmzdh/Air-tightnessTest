# MES 平台对接配置与数据推送说明

## 1. 前置条件与基础认识
- 应用启动时会在 `Config/app.json` 读取 MES 相关参数；若文件缺失，系统会按默认值生成并保存。
- 运行账户需要对 `Config` 目录拥有读写权限，以便写入默认配置和后续调整。
- MES 相关参数位于 `AppConfig`，其中与 MES 对接直接相关的字段如下：
  - **MesServerIp**：MES 服务端 IP 或主机名，用于建立 TCP/UDP 连接与 HTTP 请求。默认值为 `127.0.0.1`，若读取到空值会自动回退到该地址并写回配置。
  - **MesServerPort**：MES 服务端端口，既用于 TCP/UDP 连接，也作为 HTTP API 端口。默认值为 `8080`，若配置小于等于 0 会回退到默认值并写回配置。
  - **MesProtocol**：数据发送通道，支持 `TCP` 或 `UDP`。默认值为 `TCP`，配置会被统一转换为大写，仅接受 `TCP/UDP`。
- 其余字段（例如 `LastWorkOrder`、`LastProductModel`）为可选项，仅用于界面记忆，不影响 MES 通信，可按需保留或清空。

## 2. TCP 与 UDP 通信配置差异
### TCP 模式
1. 将 `MesProtocol` 设为 `TCP`（或留空，系统会自动回退到 `TCP`）。
2. `ConnectAsync` 会创建 `TcpClient`，在 3 秒超时窗口内尝试握手，成功后才认定连接已建立。
3. `SimulatePushAsync`、`SendTestRecordAsync` 会通过 `HTTP POST` 将 JSON 数据发送到 `http://{MesServerIp}:{MesServerPort}/api/mes/test-records`，需确保 MES 暴露对应 API 并返回 `2xx` 表示成功。

### UDP 模式
1. 将 `MesProtocol` 设为 `UDP`。
2. `ConnectAsync` 调用 `UdpClient.Connect` 检查目标地址是否可达，不会建立持久连接或校验远端响应。
3. 心跳与正式数据推送均直接发送 JSON 文本的 UDP 报文到配置的主机与端口，不涉及 HTTP 层，MES 需自行监听并确认接收。

两种模式均在失败时写入日志并回调连接状态，可用于 UI 或监控。

## 3. 配置示例（`Config/app.json`）
```json
{
  "PLCIPAddress": "192.168.1.188",
  "PLCPort": 502,
  "PLCStationId": 1,
  "AutoSave": true,
  "LastWorkOrder": "",
  "LastProductModel": "",
  "MesServerIp": "10.10.8.25",
  "MesServerPort": 9000,
  "MesProtocol": "UDP"
}
```
- **必填**：`MesServerIp`、`MesServerPort`、`MesProtocol`（以及 PLC 侧字段若需与测试设备通讯）。
- **可选**：`LastWorkOrder`、`LastProductModel` 等界面记忆项；缺省会被重置为空字符串。

## 4. 参数调整与测试流程建议
1. **确认前置条件**
   - 向 MES 平台索取通信模式、IP、端口与 API 路径（如使用 TCP/HTTP）。
   - 保证测试工站能访问 MES 网络并开放对应端口。
2. **配置修改**
   - 编辑 `Config/app.json`，填入 MES 提供的地址与端口，并设定协议；保存后重启应用以加载新参数。
   - 若需在运行中调整，可调用 `SaveAppConfigAsync` 触发写入，服务会自动校验并格式化值。
3. **连通性验证**
   - 在应用或脚本中调用 `ConnectAsync` 检查是否能够建立 TCP/UDP 通路；失败时捕获异常或查看连接状态回调。
   - 连接成功后执行 `SimulatePushAsync` 发送心跳 JSON，确认 MES 能接收并解析；TCP 模式下应获得 HTTP `2xx` 响应，UDP 模式需在 MES 侧抓包/监控确认报文。
4. **实测数据推送**
   - 使用真实 `TestRecord` 调用 `SendTestRecordAsync`，验证业务字段；不同协议会复用前述通路发送完整载荷。
5. **日志排查**
   - 所有连接与推送异常会写入 `Logs/System_YYYYMMDD.log`；`LogService` 会在失败时附加异常详情，便于排查网络/格式问题。
   - 如需历史记录，可通过 `GetRecentEntries` 查询内存缓存，或直接打开日志文件定位最近的错误栈。

## 5. 推送数据结构详解
### 5.1 模拟推送（连通性测试）
- `SimulatePushAsync` 在成功连接后会生成仅含时间戳与固定消息 `"MES模拟推送"` 的心跳对象，并按配置通道发送。
- 报文示例：
  ```json
  {
    "timestamp": "2024-05-20T08:15:30.1234567Z",
    "message": "MES模拟推送"
  }
  ```
  > 实际时间戳为 UTC 时间。

### 5.2 正式测试记录报文
`SendTestRecordAsync` 将完整的 `TestRecord` 序列化为 JSON，属性使用 `camelCase`，忽略 `null` 字段，并将枚举值序列化为字符串（如 `Pass`、`Fail`）。主要字段包括：

1. **测试概况**：`testTime`、`workOrder`、`productModel`、`productCode`、`operator`、`channel`、`testCount`、`testVoltage`、`sleepCurrent`、`staticCurrent`、`result` 等。
2. **失败与耗时信息**：`failReason`、`testDuration`。
3. **腰托动作明细 `lumbarResults`**：每项包含动作顺序 `order`、动作类型 `action`（如 `SimultaneousInflate`）、目标/实际高度、目标/实际时间、峰值/平均电流、`passed` 标记与备注。
4. **按摩点结果 `massageResults`**：记录顺序、阀门/压力开关触发状态、持续时间、峰值/平均电流、触发次数、高电平时长统计、判定结果与说明。
5. **阶段过程追踪 `stageResults`**：针对各 `TestStage`（扫码、腰托测试、按摩测试等）保存阶段状态、起止时间、提示信息、峰值/平均电流、测得高度。
6. **可选电流采样曲线**：`currentTimeline`、`lumbarCurrents`、`massageCurrents` 等集合属性若有数据也会包含在顶层 JSON；因序列化策略忽略 `null`，未赋值时不会出现在报文中。

### 5.3 传输通道差异
- **TCP/HTTP**：通过 `HttpClient` 以 `application/json` POST 到固定路径，收到非成功状态码会抛出异常，便于日志记录与重试。
- **UDP**：将同样的 JSON 字符串直接发送到指定 IP/端口，因 UDP 无应答，MES 需自行监听并确认接收。

## 6. 常见问题排查
- 若连接失败，检查 MES IP/端口是否正确、网络是否连通、防火墙是否放行；必要时使用 `ping`/`telnet` 或抓包工具验证。
- TCP 模式下收到非 `2xx` 响应时，查看 MES API 日志确认请求格式；应用端可在日志中找到完整的异常与响应内容。
- UDP 模式下如 MES 未收到报文，可在工站使用网络抓包工具确认数据是否发出，并让 MES 端确认监听端口。

通过以上说明，MES 平台同事可完成参数配置、切换通信模式，并了解推送数据结构与排错方式。
