using System.IO.Pipelines;
using System.Text.Json;
using GrowthInspector.Agents.Mcp;
using GrowthInspector.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using McpServerEntity = GrowthInspector.Core.Models.McpServer;
using McpServerProtocol = ModelContextProtocol.Server.McpServer;

namespace GrowthInspector.Tests;

/// <summary>
/// 用内存流跑真实的 MCP JSON-RPC 握手，验证连接工厂把 tool list / call 转成 Core 契约，
/// 不依赖外部进程或网络。
/// </summary>
public sealed class McpServerConnectionFactoryTests
{
    [Fact]
    public async Task ConnectAsync_ListsThreeToolsAndCallsQueryMetric()
    {
        var serverToClient = new Pipe();
        var clientToServer = new Pipe();

        var toolCollection = new McpServerPrimitiveCollection<McpServerTool>(StringComparer.Ordinal);
        toolCollection.Add(McpServerTool.Create(
            (Func<string, string, string>)((metric, window) => JsonSerializer.Serialize(new
            {
                metric,
                window,
                value = 321.5,
                series = Array.Empty<object>(),
            })),
            new McpServerToolCreateOptions { Name = "QueryMetric", Description = "查询指标时序" }));
        toolCollection.Add(McpServerTool.Create(
            (Func<string, string>)(sql => JsonSerializer.Serialize(new
            {
                rows = new[] { new { id = 1 }, new { id = 2 } },
                rowCount = 2,
            })),
            new McpServerToolCreateOptions { Name = "QuerySql", Description = "执行只读查询" }));
        toolCollection.Add(McpServerTool.Create(
            (Func<string, string>)(service => JsonSerializer.Serialize(new { service, status = "up" })),
            new McpServerToolCreateOptions { Name = "GetServiceHealth", Description = "查询服务健康状态" }));

        var serverOptions = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "in-process-mock", Version = "1.0.0" },
            ToolCollection = toolCollection,
        };
        var serverTransport = new StreamServerTransport(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream(),
            "in-process-mock",
            NullLoggerFactory.Instance);

        var server = McpServerProtocol.Create(serverTransport, serverOptions, NullLoggerFactory.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var serverTask = server.RunAsync(cancellation.Token);
        IMcpServerConnection? connection = null;

        try
        {
            var factory = new McpServerConnectionFactory(
                NullLoggerFactory.Instance,
                _ => new StreamClientTransport(
                    clientToServer.Writer.AsStream(),
                    serverToClient.Reader.AsStream(),
                    NullLoggerFactory.Instance));

            connection = await factory.ConnectAsync(
                new McpServerEntity
                {
                    Name = "in-process-mock",
                    Transport = McpTransport.Stdio,
                    TimeoutSeconds = 10,
                },
                cancellation.Token);

            var tools = await connection.ListToolsAsync(cancellation.Token);
            Assert.Equal(3, tools.Count);
            Assert.Contains(tools, tool => tool.Name == "QueryMetric");
            Assert.Contains(tools, tool => tool.Name == "QuerySql");
            Assert.Contains(tools, tool => tool.Name == "GetServiceHealth");

            var metric = await connection.CallToolAsync(
                "QueryMetric",
                """{"metric":"http_p99_latency_ms","window":"1h"}""",
                cancellation.Token);
            Assert.False(metric.IsError);
            using var metricJson = JsonDocument.Parse(metric.Text);
            Assert.Equal("http_p99_latency_ms", metricJson.RootElement.GetProperty("metric").GetString());
            Assert.Equal("1h", metricJson.RootElement.GetProperty("window").GetString());
            Assert.Equal(321.5, metricJson.RootElement.GetProperty("value").GetDouble());

            var health = await connection.CallToolAsync(
                "GetServiceHealth",
                """{"service":"api"}""",
                cancellation.Token);
            Assert.False(health.IsError);
            using var healthJson = JsonDocument.Parse(health.Text);
            Assert.Equal("up", healthJson.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }

            cancellation.Cancel();
            try
            {
                await serverTask;
            }
            catch (OperationCanceledException)
            {
                // 正常退出。
            }

            await server.DisposeAsync();
        }
    }
}
