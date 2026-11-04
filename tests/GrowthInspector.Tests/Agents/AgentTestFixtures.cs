using GrowthInspector.Agents.Tools;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Core.Options;
using GrowthInspector.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Environment = GrowthInspector.Core.Models.Environment;

namespace GrowthInspector.Tests.Agents;

/// <summary>测试用 DbContextFactory：每次创建独立上下文，指向同一个临时 SQLite 文件。</summary>
public sealed class TestDbContextFactory(DbContextOptions<GrowthInspectorDbContext> options)
    : IDbContextFactory<GrowthInspectorDbContext>
{
    public GrowthInspectorDbContext CreateDbContext() => new(options);

    public Task<GrowthInspectorDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}

/// <summary>临时 SQLite 库，测试结束删除文件。</summary>
public sealed class TestSqliteDatabase : IDisposable
{
    public TestSqliteDatabase()
    {
        DatabasePath = Path.Combine(Path.GetTempPath(), $"growth-inspector-agents-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        Options = new DbContextOptionsBuilder<GrowthInspectorDbContext>()
            .UseSqlite(connectionString)
            .Options;
        Factory = new TestDbContextFactory(Options);

        using var db = Factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public string DatabasePath { get; }

    public DbContextOptions<GrowthInspectorDbContext> Options { get; }

    public TestDbContextFactory Factory { get; }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(DatabasePath))
        {
            File.Delete(DatabasePath);
        }
    }
}

public sealed record AgentSeed(
    Project Project,
    Environment Environment,
    Checklist Checklist,
    ChecklistItem Item,
    ChecklistRun Run);

/// <summary>测试数据装配：Project / Environment / Checklist / Item / Run 以及手册。</summary>
public static class AgentTestData
{
    public static async Task<AgentSeed> SeedAsync(TestSqliteDatabase database)
    {
        await using var db = await database.Factory.CreateDbContextAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var project = new Project { Name = "测试项目", Code = "test-" + suffix };
        var environment = new Environment { Name = "prod", Project = project, IsDefault = true };
        var checklist = new Checklist
        {
            Name = "测试检查单",
            Project = project,
            Frequency = ChecklistFrequency.Manual,
        };
        var item = new ChecklistItem { Title = "测试检查项", Checklist = checklist, SortOrder = 0 };
        checklist.Items.Add(item);
        project.Environments.Add(environment);

        db.Projects.Add(project);
        db.Checklists.Add(checklist);
        await db.SaveChangesAsync();

        var run = new ChecklistRun
        {
            ChecklistId = checklist.Id,
            EnvironmentId = environment.Id,
            SnapshotJson = "{}",
            Status = RunStatus.Running,
        };
        db.ChecklistRuns.Add(run);
        await db.SaveChangesAsync();

        return new AgentSeed(project, environment, checklist, item, run);
    }

    public static async Task<Runbook> AddRunbookAsync(
        TestSqliteDatabase database,
        Guid projectId,
        string title,
        string body,
        RunbookKind kind = RunbookKind.Specialized,
        bool archived = false)
    {
        await using var db = await database.Factory.CreateDbContextAsync();
        var runbook = new Runbook
        {
            ProjectId = projectId,
            Title = title,
            Body = body,
            Kind = kind,
            IsArchived = archived,
        };
        db.Runbooks.Add(runbook);
        await db.SaveChangesAsync();
        return runbook;
    }

    public static async Task<McpServer> AddMcpServerAsync(
        TestSqliteDatabase database,
        Guid projectId,
        string name,
        Guid? environmentId = null,
        bool enabled = true,
        bool archived = false)
    {
        await using var db = await database.Factory.CreateDbContextAsync();
        var server = new McpServer
        {
            ProjectId = projectId,
            EnvironmentId = environmentId,
            Name = name,
            IsEnabled = enabled,
            IsArchived = archived,
        };
        db.McpServers.Add(server);
        await db.SaveChangesAsync();
        return server;
    }

    public static AgentToolsetFactory CreateToolsetFactory(
        TestSqliteDatabase database,
        IMcpToolInvoker? mcpToolInvoker = null,
        IRunbookWriteService? runbookWriteService = null,
        InspectionOptions? options = null,
        bool enableFtsIndex = true)
    {
        var inspectionOptions = Options.Create(options ?? new InspectionOptions());
        var search = new RunbookSearchService(
            database.Factory,
            inspectionOptions,
            NullLogger<RunbookSearchService>.Instance,
            enableFtsIndex);
        var auditor = new ToolCallAuditor(
            database.Factory,
            inspectionOptions,
            NullLogger<ToolCallAuditor>.Instance);

        return new AgentToolsetFactory(
            database.Factory,
            mcpToolInvoker ?? new FakeMcpToolInvoker(),
            runbookWriteService,
            search,
            auditor,
            inspectionOptions,
            NullLoggerFactory.Instance);
    }
}

/// <summary>可控的 MCP 调用器：记录每次调用与上下文，按需返回结果。</summary>
public sealed class FakeMcpToolInvoker : IMcpToolInvoker
{
    public Dictionary<Guid, IReadOnlyList<McpToolDescriptor>> ToolLists { get; } = [];

    public Func<Guid, string, string, McpToolInvocationContext?, McpToolCallResult>? OnCall { get; set; }

    public List<McpCall> Calls { get; } = [];

    public Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(
        Guid mcpServerId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(ToolLists.TryGetValue(mcpServerId, out var tools)
            ? tools
            : (IReadOnlyList<McpToolDescriptor>)[]);

    public Task<McpToolCallResult> CallToolAsync(
        Guid mcpServerId,
        string toolName,
        string argumentsJson,
        McpToolInvocationContext? context = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(new McpCall(mcpServerId, toolName, argumentsJson, context));
        var result = OnCall?.Invoke(mcpServerId, toolName, argumentsJson, context)
            ?? new McpToolCallResult(true, """{"value":1}""", false, 1);
        return Task.FromResult(result);
    }

    public sealed record McpCall(
        Guid McpServerId,
        string ToolName,
        string ArgumentsJson,
        McpToolInvocationContext? Context);
}

/// <summary>记录改写调用；可配置失败或异常。</summary>
public sealed class FakeRunbookWriteService : IRunbookWriteService
{
    public List<WriteCall> Calls { get; } = [];

    public Func<Guid, string?, string, RunbookSectionUpdateResult>? OnUpdate { get; set; }

    public Task<RunbookSectionUpdateResult> UpdateSectionAsync(
        Guid runbookId,
        string? sectionHeading,
        string newContent,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(new WriteCall(runbookId, sectionHeading, newContent));
        if (OnUpdate is null)
        {
            return Task.FromResult(new RunbookSectionUpdateResult(true));
        }

        return Task.FromResult(OnUpdate(runbookId, sectionHeading, newContent));
    }

    public sealed record WriteCall(Guid RunbookId, string? Section, string Content);
}

/// <summary>按脚本返回 ChatResponse 的假模型；也可用 Handler 模拟超时或异常。</summary>
public sealed class ScriptedChatClient : IChatClient
{
    private readonly Queue<ChatResponse> script = new();

    public Func<IReadOnlyList<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>>? Handler { get; set; }

    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public List<ChatOptions?> ReceivedOptions { get; } = [];

    public ScriptedChatClient Enqueue(ChatResponse response)
    {
        script.Enqueue(response);
        return this;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = messages.ToList();
        Requests.Add(snapshot);
        ReceivedOptions.Add(options);

        if (Handler is not null)
        {
            return Handler(snapshot, options, cancellationToken);
        }

        if (script.Count == 0)
        {
            throw new InvalidOperationException("测试脚本已用尽。");
        }

        return Task.FromResult(script.Dequeue());
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("执行 agent 只使用非流式调用。");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    public static ChatResponse Text(string text)
        => new(new ChatMessage(ChatRole.Assistant, text));

    public static ChatResponse FunctionCalls(params FunctionCallContent[] calls)
        => new(new ChatMessage(
            ChatRole.Assistant,
            calls.Cast<AIContent>().ToList()));
}
