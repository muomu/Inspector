using System.Text;
using GrowthInspector.Agents.Mcp;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Core.Options;
using GrowthInspector.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Environment = GrowthInspector.Core.Models.Environment;

namespace GrowthInspector.Tests;

/// <summary>MCP 工具调用入口：连接复用、工具刷新、审计截断、错误分支与多 server 隔离。</summary>
public sealed class McpToolInvokerTests : IAsyncLifetime
{
    private const string MockServerName = "mock-metrics";

    private readonly string databasePath =
        Path.Combine(Path.GetTempPath(), $"growth-inspector-mcp-{Guid.NewGuid():N}.db");

    private readonly FakeConnectionFactory connectionFactory = new();
    private ServiceProvider serviceProvider = null!;
    private IDbContextFactory<GrowthInspectorDbContext> dbContextFactory = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddGrowthInspectorData(databasePath);
        serviceProvider = services.BuildServiceProvider();
        await serviceProvider.EnsureGrowthInspectorDatabaseAsync();
        dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<GrowthInspectorDbContext>>();
    }

    [Fact]
    public async Task ListToolsAsync_ReturnsServerTools_AndRefreshesOnEveryCall()
    {
        var server = await SeedServerAsync();
        var connection = RegisterConnection(server.Id);
        connection.Tools.AddRange(CreateMockTools());

        var invoker = CreateInvoker();

        var first = await invoker.ListToolsAsync(server.Id);
        var second = await invoker.ListToolsAsync(server.Id);

        Assert.Equal(3, first.Count);
        Assert.Equal(3, second.Count);
        Assert.Equal(2, connection.ListToolsCalls);
        Assert.Contains(first, tool => tool.Name == "QueryMetric"
            && tool.InputSchemaJson is not null
            && tool.InputSchemaJson.Contains("\"metric\"", StringComparison.Ordinal));
        Assert.Contains(first, tool => tool.Name == "QuerySql");
        Assert.Contains(first, tool => tool.Name == "GetServiceHealth");
    }

    [Fact]
    public async Task CallToolAsync_QueryMetric_WritesToolCallLog()
    {
        var server = await SeedServerAsync();
        var connection = RegisterConnection(server.Id);
        connection.Tools.AddRange(CreateMockTools());
        connection.CallHandler = (toolName, argumentsJson, _) => Task.FromResult(
            new McpToolCallOutcome(
                IsError: false,
                """{"metric":"http_p99_latency_ms","window":"1h","value":321.5,"series":[]}"""));

        var (runId, itemId) = await SeedRunAsync();
        var invoker = CreateInvoker();

        var result = await invoker.CallToolAsync(
            server.Id,
            "QueryMetric",
            """{"metric":"http_p99_latency_ms","window":"1h"}""",
            new McpToolInvocationContext(runId, itemId, AgentRole.Execution));

        Assert.True(result.Succeeded);
        Assert.False(result.Truncated);
        Assert.Contains("\"value\":321.5", result.ResultText, StringComparison.Ordinal);
        Assert.Null(result.ErrorMessage);

        await using var db = await dbContextFactory.CreateDbContextAsync();
        var log = await db.ToolCallLogs.SingleAsync();
        Assert.Equal(runId, log.ChecklistRunId);
        Assert.Equal(itemId, log.ChecklistItemId);
        Assert.Equal(AgentRole.Execution, log.AgentRole);
        Assert.Equal(server.Id, log.McpServerId);
        Assert.Equal(MockServerName, log.ServerName);
        Assert.Equal("QueryMetric", log.ToolName);
        Assert.Contains("http_p99_latency_ms", log.Arguments);
        Assert.Contains("321.5", log.Result);
        Assert.True(log.Succeeded);
        Assert.False(log.Truncated);
        Assert.True(log.DurationMs >= 0);
    }

    [Fact]
    public async Task CallToolAsync_TruncatesOversizedResultAtEightKilobytes()
    {
        var server = await SeedServerAsync();
        var connection = RegisterConnection(server.Id);
        connection.Tools.AddRange(CreateMockTools());
        connection.CallHandler = (_, _, _) => Task.FromResult(
            new McpToolCallOutcome(IsError: false, new string('中', 4000)));

        var (runId, _) = await SeedRunAsync();
        var invoker = CreateInvoker();

        var result = await invoker.CallToolAsync(
            server.Id,
            "QuerySql",
            """{"sql":"select 1"}""",
            new McpToolInvocationContext(runId));

        Assert.True(result.Succeeded);
        Assert.True(result.Truncated);
        Assert.NotNull(result.ResultText);
        Assert.True(Encoding.UTF8.GetByteCount(result.ResultText) <= 8192);
        Assert.EndsWith("中", result.ResultText, StringComparison.Ordinal);

        await using var db = await dbContextFactory.CreateDbContextAsync();
        var log = await db.ToolCallLogs.SingleAsync();
        Assert.True(log.Truncated);
        Assert.NotNull(log.Result);
        Assert.True(Encoding.UTF8.GetByteCount(log.Result) <= 8192);
    }

    [Fact]
    public async Task CallToolAsync_UsesGlobalLimitWhenItIsSmallerThanServerLimit()
    {
        var server = await SeedServerAsync(maxResultBytes: 8192);
        var connection = RegisterConnection(server.Id);
        connection.Tools.AddRange(CreateMockTools());
        connection.CallHandler = (_, _, _) => Task.FromResult(
            new McpToolCallOutcome(IsError: false, new string('a', 5000)));

        var invoker = CreateInvoker(new InspectionOptions { ToolResultMaxBytes = 1024 });
        var result = await invoker.CallToolAsync(server.Id, "QuerySql", """{"sql":"select 1"}""");

        Assert.True(result.Truncated);
        Assert.Equal(1024, result.ResultText!.Length);
    }

    [Fact]
    public async Task CallToolAsync_ReturnsStructuredFailureForUnknownDisabledAndMissingTool()
    {
        var invoker = CreateInvoker();

        var unknown = await invoker.CallToolAsync(Guid.NewGuid(), "QueryMetric", "{}");
        Assert.False(unknown.Succeeded);
        Assert.Contains("未登记", unknown.ErrorMessage, StringComparison.Ordinal);

        var disabledServer = await SeedServerAsync("disabled-server", isEnabled: false);
        RegisterConnection(disabledServer.Id).Tools.AddRange(CreateMockTools());
        var disabled = await invoker.CallToolAsync(disabledServer.Id, "QueryMetric", "{}");
        Assert.False(disabled.Succeeded);
        Assert.Contains("已禁用", disabled.ErrorMessage, StringComparison.Ordinal);

        var server = await SeedServerAsync("only-sql");
        var connection = RegisterConnection(server.Id);
        connection.Tools.Add(new McpToolDescriptor("QuerySql", "只读查询", """{"type":"object"}"""));
        var missing = await invoker.CallToolAsync(server.Id, "QueryMetric", "{}");
        Assert.False(missing.Succeeded);
        Assert.Contains("没有工具", missing.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, connection.CallToolCalls);
    }

    [Fact]
    public async Task CallToolAsync_ConnectionFailure_ReturnsStructuredFailure()
    {
        var server = await SeedServerAsync("broken-server");
        // 不注册 fake 连接，连接工厂会抛错。
        var invoker = CreateInvoker();

        var result = await invoker.CallToolAsync(server.Id, "QueryMetric", "{}");

        Assert.False(result.Succeeded);
        Assert.Contains("没有为该 server 配置 fake 连接", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallToolAsync_Timeout_ReturnsStructuredFailure()
    {
        var server = await SeedServerAsync("slow-server", timeoutSeconds: 1);
        var connection = RegisterConnection(server.Id);
        connection.Tools.AddRange(CreateMockTools());
        connection.CallHandler = async (_, _, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new McpToolCallOutcome(IsError: false, "{}");
        };

        var invoker = CreateInvoker();
        var result = await invoker.CallToolAsync(server.Id, "QueryMetric", "{}");

        Assert.False(result.Succeeded);
        Assert.Contains("超时", result.ErrorMessage, StringComparison.Ordinal);
        Assert.True(result.DurationMs < 10_000);
    }

    [Fact]
    public async Task CallToolAsync_ToolErrorOutcome_MarksFailureAndWritesAudit()
    {
        var server = await SeedServerAsync();
        var connection = RegisterConnection(server.Id);
        connection.Tools.AddRange(CreateMockTools());
        connection.CallHandler = (_, _, _) => Task.FromResult(
            new McpToolCallOutcome(IsError: true, "upstream boom", "upstream boom"));

        var (runId, _) = await SeedRunAsync();
        var invoker = CreateInvoker();
        var result = await invoker.CallToolAsync(
            server.Id,
            "QueryMetric",
            "{}",
            new McpToolInvocationContext(runId));

        Assert.False(result.Succeeded);
        Assert.Contains("upstream boom", result.ErrorMessage, StringComparison.Ordinal);

        await using var db = await dbContextFactory.CreateDbContextAsync();
        var log = await db.ToolCallLogs.SingleAsync();
        Assert.False(log.Succeeded);
        Assert.Contains("upstream boom", log.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallToolAsync_WithoutContext_SkipsAudit()
    {
        var server = await SeedServerAsync();
        var connection = RegisterConnection(server.Id);
        connection.Tools.AddRange(CreateMockTools());
        connection.CallHandler = (_, _, _) => Task.FromResult(new McpToolCallOutcome(IsError: false, "{}"));

        var invoker = CreateInvoker();
        var result = await invoker.CallToolAsync(server.Id, "QueryMetric", "{}");

        Assert.True(result.Succeeded);
        await using var db = await dbContextFactory.CreateDbContextAsync();
        Assert.Equal(0, await db.ToolCallLogs.CountAsync());
    }

    [Fact]
    public async Task ListToolsAsync_KeepsToolListsPerServer()
    {
        var serverA = await SeedServerAsync("server-a");
        var connectionA = RegisterConnection(serverA.Id);
        connectionA.Tools.AddRange(CreateMockTools());

        var serverB = await SeedServerAsync("server-b");
        var connectionB = RegisterConnection(serverB.Id);
        connectionB.Tools.Add(new McpToolDescriptor("QuerySql", "只读查询", """{"type":"object"}"""));

        var invoker = CreateInvoker();
        var toolsA = await invoker.ListToolsAsync(serverA.Id);
        var toolsB = await invoker.ListToolsAsync(serverB.Id);
        var toolsAAgain = await invoker.ListToolsAsync(serverA.Id);

        Assert.Equal(["GetServiceHealth", "QueryMetric", "QuerySql"], toolsA.Select(tool => tool.Name).Order());
        Assert.Equal(["QuerySql"], toolsB.Select(tool => tool.Name));
        Assert.Equal(toolsA.Select(tool => tool.Name), toolsAAgain.Select(tool => tool.Name));
    }

    [Fact]
    public async Task ListToolsAsync_ConnectionFailure_ReturnsEmptyWithoutThrowing()
    {
        var server = await SeedServerAsync("unreachable-server");
        var invoker = CreateInvoker();

        var tools = await invoker.ListToolsAsync(server.Id);

        Assert.Empty(tools);
    }

    private McpToolInvoker CreateInvoker(InspectionOptions? options = null)
        => new(
            dbContextFactory,
            connectionFactory,
            Options.Create(options ?? new InspectionOptions()),
            NullLogger<McpToolInvoker>.Instance);

    private FakeMcpServerConnection RegisterConnection(Guid serverId)
    {
        var connection = new FakeMcpServerConnection();
        connectionFactory.Connections[serverId] = connection;
        return connection;
    }

    private static McpToolDescriptor[] CreateMockTools() =>
    [
        new("QueryMetric", "查询指标时序", """{"type":"object","properties":{"metric":{"type":"string"},"window":{"type":"string"}}}"""),
        new("QuerySql", "执行只读查询", """{"type":"object","properties":{"sql":{"type":"string"}}}"""),
        new("GetServiceHealth", "查询服务健康状态", """{"type":"object","properties":{"service":{"type":"string"}}}"""),
    ];

    private async Task<McpServer> SeedServerAsync(
        string name = MockServerName,
        bool isEnabled = true,
        int maxResultBytes = 8192,
        int timeoutSeconds = 60)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync();
        var project = new Project
        {
            Name = $"MCP 项目-{Guid.NewGuid():N}",
            Code = Guid.NewGuid().ToString("N")[..8],
        };
        var server = new McpServer
        {
            Project = project,
            Name = name,
            Transport = McpTransport.Stdio,
            Command = "mock",
            IsEnabled = isEnabled,
            MaxResultBytes = maxResultBytes,
            TimeoutSeconds = timeoutSeconds,
        };

        db.Projects.Add(project);
        db.McpServers.Add(server);
        await db.SaveChangesAsync();
        return server;
    }

    private async Task<(Guid RunId, Guid ItemId)> SeedRunAsync()
    {
        await using var db = await dbContextFactory.CreateDbContextAsync();
        var project = new Project
        {
            Name = $"执行项目-{Guid.NewGuid():N}",
            Code = Guid.NewGuid().ToString("N")[..8],
        };
        var environment = new Environment { Name = $"env-{Guid.NewGuid():N}", Project = project, IsDefault = true };
        var checklist = new Checklist { Name = $"检查单-{Guid.NewGuid():N}", Project = project };
        var item = new ChecklistItem { Title = $"检查项-{Guid.NewGuid():N}", Checklist = checklist };
        var run = new ChecklistRun
        {
            Checklist = checklist,
            Environment = environment,
            SnapshotJson = "{}",
            Status = RunStatus.Succeeded,
        };

        db.Projects.Add(project);
        db.Environments.Add(environment);
        db.Checklists.Add(checklist);
        db.ChecklistItems.Add(item);
        db.ChecklistRuns.Add(run);
        await db.SaveChangesAsync();
        return (run.Id, item.Id);
    }

    public async Task DisposeAsync()
    {
        await serviceProvider.DisposeAsync();
        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }
    }

    /// <summary>按 server Id 返回预置的假连接；未预置时按连接失败处理。</summary>
    private sealed class FakeConnectionFactory : IMcpServerConnectionFactory
    {
        public Dictionary<Guid, FakeMcpServerConnection> Connections { get; } = [];

        public Task<IMcpServerConnection> ConnectAsync(McpServer server, CancellationToken cancellationToken = default)
        {
            if (!Connections.TryGetValue(server.Id, out var connection))
            {
                throw new McpConnectionException("没有为该 server 配置 fake 连接。");
            }

            return Task.FromResult<IMcpServerConnection>(connection);
        }
    }

    private sealed class FakeMcpServerConnection : IMcpServerConnection
    {
        public List<McpToolDescriptor> Tools { get; } = [];

        public Func<string, string, CancellationToken, Task<McpToolCallOutcome>> CallHandler { get; set; }
            = (_, _, _) => Task.FromResult(new McpToolCallOutcome(IsError: false, "{}"));

        public int ListToolsCalls { get; private set; }

        public int CallToolCalls { get; private set; }

        public Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default)
        {
            ListToolsCalls++;
            return Task.FromResult<IReadOnlyList<McpToolDescriptor>>(Tools.ToList());
        }

        public Task<McpToolCallOutcome> CallToolAsync(
            string toolName,
            string argumentsJson,
            CancellationToken cancellationToken = default)
        {
            CallToolCalls++;
            return CallHandler(toolName, argumentsJson, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
