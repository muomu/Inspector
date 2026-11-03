// GrowthInspector.MockMcpServer —— v1 端到端验收用的 mock MCP server（stdio 传输）。
//
// TODO(TPG-2703): 用 ModelContextProtocol 实现 stdio JSON-RPC server，暴露 3 个 mock 工具：
//   growth.metrics.query    指标时序（JSON）
//   growth.logs.query       日志行（JSON）
//   growth.service.health   服务健康快照（JSON）
//
// 实现约束（来自 spike 结论）：服务端控制台日志必须重定向到 stderr，
// 否则会污染 stdout 的 JSON-RPC 流，导致客户端握手失败。
Console.Error.WriteLine("GrowthInspector.MockMcpServer：骨架占位，stdio MCP server 由 TPG-2703 实现。");
return 1;
