// GrowthInspector.MockMcpServer —— v1 端到端验收用的 mock MCP server（stdio 传输）。
//
// 约束：stdout 只用于 MCP JSON-RPC 协议，日志与控制台输出全部走 stderr，
// 否则会污染协议流，导致客户端握手失败。

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "growth-inspector-mock-mcp",
            Version = "1.0.0",
        };
        options.ServerInstructions = "Tradepass Growth Inspector 的离线 mock MCP server，仅用于 v1 联调与验收。";
    })
    .WithStdioServerTransport()
    .WithTools(
    [
        McpServerTool.Create(
            (Func<string, string, string>)MockTools.QueryMetric,
            new McpServerToolCreateOptions
            {
                Name = "QueryMetric",
                Description = "查询指标在时间窗口内的时序数据，返回 {metric,window,value,series}。",
            }),
        McpServerTool.Create(
            (Func<string, string>)MockTools.QuerySql,
            new McpServerToolCreateOptions
            {
                Name = "QuerySql",
                Description = "执行只读查询，返回 {rows,rowCount}。",
            }),
        McpServerTool.Create(
            (Func<string, string>)MockTools.GetServiceHealth,
            new McpServerToolCreateOptions
            {
                Name = "GetServiceHealth",
                Description = "查询服务健康状态，返回 {service,status}；status 为 healthy / degraded / up / down。",
            }),
    ]);

await builder.Build().RunAsync();

internal static class MockTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string QueryMetric(string metric, string window)
    {
        var latestValue = metric switch
        {
            "http_p99_latency_ms" => 321.5,
            "cpu_usage_percent" => 62.0,
            "error_rate_percent" => 0.8,
            "qps" => 1280.0,
            _ => 42.0,
        };

        var now = DateTimeOffset.UtcNow;
        var series = Enumerable.Range(0, 12)
            .Select(index => new
            {
                timestamp = now.AddMinutes(-(11 - index) * 5).ToString("O"),
                value = Math.Round(latestValue * (0.85 + index * 0.0125), 2),
            })
            .ToArray();

        return JsonSerializer.Serialize(
            new
            {
                metric,
                window,
                value = latestValue,
                series,
            },
            JsonOptions);
    }

    public static string QuerySql(string sql)
    {
        var rows = new[]
        {
            new { id = 1, service = "checkout", status = "healthy" },
            new { id = 2, service = "payment", status = "healthy" },
            new { id = 3, service = "search", status = "degraded" },
        };

        return JsonSerializer.Serialize(new { rows, rowCount = rows.Length }, JsonOptions);
    }

    public static string GetServiceHealth(string service)
    {
        // 口径与知识资产（seed）里检查项断言一致：
        // - 依赖类（MySQL / Redis / Kafka / StarRocks）返回 up；
        // - 服务进程类默认 healthy；示例故障服务返回 degraded / down，便于演示异常与恢复。
        var normalized = (service ?? string.Empty).Trim().ToLowerInvariant();
        var status = normalized switch
        {
            "mysql" or "redis" or "kafka" or "starrocks" => "up",
            "database" or "payment" => "down",
            "search" => "degraded",
            _ => "healthy",
        };

        return JsonSerializer.Serialize(new { service, status }, JsonOptions);
    }
}
