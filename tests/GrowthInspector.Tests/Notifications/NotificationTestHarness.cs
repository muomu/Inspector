using System.Text.Json;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Core.Options;
using GrowthInspector.Data;
using GrowthInspector.Notifications;
using GrowthInspector.Notifications.Lark;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Environment = GrowthInspector.Core.Models.Environment;

namespace GrowthInspector.Tests.Notifications;

/// <summary>可推进的测试时钟：finding / 日报时间都从 TimeProvider 读取。</summary>
internal sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan delta) => UtcNow += delta;
}

/// <summary>记录真实通道路径的假 Lark 客户端；mock 模式下不应被调用。</summary>
internal sealed class RecordingLarkNotifier : ILarkNotifier
{
    public List<(string WebhookUrl, string Markdown)> Sent { get; } = [];

    public bool ThrowOnSend { get; set; }

    public Task SendMarkdownAsync(string webhookUrl, string markdown, CancellationToken cancellationToken = default)
    {
        if (ThrowOnSend)
        {
            throw new HttpRequestException("fake Lark 通道不可用");
        }

        Sent.Add((webhookUrl, markdown));
        return Task.CompletedTask;
    }
}

internal sealed record CheckResultSeed(
    Guid ChecklistItemId,
    CheckResultStatus Status,
    VerdictStatus? AgentVerdict,
    string? Evidence = null,
    string? Narrative = null,
    string? ErrorMessage = null,
    IReadOnlyList<string>? Observations = null,
    int ToolCallCount = 0)
{
    public static CheckResultSeed Abnormal(Guid itemId, string evidence = "value=900", string? narrative = null) =>
        new(itemId, CheckResultStatus.Abnormal, VerdictStatus.Abnormal, evidence, narrative);

    public static CheckResultSeed Healthy(Guid itemId, string evidence = "value=120") =>
        new(itemId, CheckResultStatus.Ok, VerdictStatus.Ok, evidence);

    public static CheckResultSeed Insufficient(Guid itemId, string? evidence = null) =>
        new(itemId, CheckResultStatus.InsufficientEvidence, VerdictStatus.InsufficientEvidence, evidence);
}

internal sealed record NotificationSeed(
    Project Project,
    IReadOnlyList<Environment> Environments,
    Checklist Checklist,
    ChecklistItem Item)
{
    public Environment Prod => Environments[0];
}

/// <summary>通知测试宿主：独立 SQLite 库 + 固定时钟 + 假 Lark 通道。</summary>
internal sealed class NotificationTestHarness : IAsyncDisposable
{
    private readonly string databasePath;
    private bool databaseReady;

    public NotificationTestHarness(
        Action<InspectionOptions>? configureInspection = null,
        Action<LarkNotificationOptions>? configureLark = null,
        Action<IServiceCollection>? configureServices = null)
    {
        databasePath = Path.Combine(Path.GetTempPath(), $"growth-inspector-notify-{Guid.NewGuid():N}.db");
        Clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 4, 2, 0, 0, TimeSpan.Zero));
        LarkNotifier = new RecordingLarkNotifier();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<TimeProvider>(Clock);
        services.Configure<InspectionOptions>(options =>
        {
            options.TimeZone = "UTC";
            configureInspection?.Invoke(options);
        });
        services.Configure<LarkNotificationOptions>(options => configureLark?.Invoke(options));
        services.AddGrowthInspectorData(databasePath);
        services.AddGrowthInspectorNotifications();

        // 覆盖 typed HttpClient 注册，测试走假通道。
        services.AddSingleton<ILarkNotifier>(LarkNotifier);
        configureServices?.Invoke(services);

        Services = services.BuildServiceProvider();
        DbFactory = Services.GetRequiredService<IDbContextFactory<GrowthInspectorDbContext>>();
    }

    public ServiceProvider Services { get; }

    public MutableTimeProvider Clock { get; }

    public RecordingLarkNotifier LarkNotifier { get; }

    public IDbContextFactory<GrowthInspectorDbContext> DbFactory { get; }

    public async Task EnsureDatabaseAsync()
    {
        if (databaseReady)
        {
            return;
        }

        await Services.EnsureGrowthInspectorDatabaseAsync();
        databaseReady = true;
    }

    public async Task<NotificationSeed> SeedAsync(
        int environmentCount = 1,
        Severity? severity = Severity.Critical,
        string? webhookUrl = null,
        string projectName = "示例项目",
        string itemTitle = "P99 延迟",
        string itemBody = "检查 P99 延迟是否低于 500ms；超过则先看下游超时与连接池。")
    {
        await EnsureDatabaseAsync();

        await using var db = await DbFactory.CreateDbContextAsync();
        var project = new Project
        {
            Name = projectName,
            Code = $"demo-{Guid.NewGuid():N}"[..12],
            LarkWebhookUrl = webhookUrl,
        };

        var environments = Enumerable.Range(0, environmentCount)
            .Select(index => new Environment
            {
                Name = index == 0 ? "prod" : $"beta{index}",
                IsDefault = index == 0,
            })
            .ToList();
        foreach (var environment in environments)
        {
            project.Environments.Add(environment);
        }

        var checklist = new Checklist { Name = "核心指标检查单", Project = project };
        var item = new ChecklistItem
        {
            Title = itemTitle,
            Body = itemBody,
            SortOrder = 0,
            Severity = severity,
        };
        checklist.Items.Add(item);
        db.Projects.Add(project);
        db.Checklists.Add(checklist);
        await db.SaveChangesAsync();

        return new NotificationSeed(project, environments, checklist, item);
    }

    public async Task<ChecklistItem> AddItemAsync(
        NotificationSeed seed,
        string title,
        int sortOrder,
        Severity? severity = Severity.Critical,
        string body = "检查项正文：查什么 / 什么算健康 / 异常先看哪。")
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        var item = new ChecklistItem
        {
            ChecklistId = seed.Checklist.Id,
            Title = title,
            Body = body,
            SortOrder = sortOrder,
            Severity = severity,
        };
        db.ChecklistItems.Add(item);
        await db.SaveChangesAsync();
        return item;
    }

    public async Task<Guid> AddRunAsync(
        NotificationSeed seed,
        Guid environmentId,
        IReadOnlyList<CheckResultSeed> results,
        RunStatus status = RunStatus.Succeeded,
        DateTimeOffset? startedAt = null,
        string? summary = null,
        ChecklistTrigger trigger = ChecklistTrigger.Schedule)
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        var run = new ChecklistRun
        {
            ChecklistId = seed.Checklist.Id,
            EnvironmentId = environmentId,
            SnapshotJson = "{}",
            Trigger = trigger,
            Status = status,
            StartedAt = startedAt ?? Clock.UtcNow,
            CompletedAt = status is RunStatus.Succeeded or RunStatus.Failed ? startedAt ?? Clock.UtcNow : null,
            ErrorMessage = status == RunStatus.Failed ? "检查项执行失败" : null,
        };

        foreach (var result in results)
        {
            run.Results.Add(new CheckResult
            {
                ChecklistRunId = run.Id,
                ChecklistItemId = result.ChecklistItemId,
                Status = result.Status,
                AgentVerdict = result.AgentVerdict,
                Evidence = result.Evidence,
                Narrative = result.Narrative,
                ErrorMessage = result.ErrorMessage,
                ObservationsJson = result.Observations is { Count: > 0 }
                    ? JsonSerializer.Serialize(result.Observations, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    : null,
                ToolCallCount = result.ToolCallCount,
            });
        }

        if (!string.IsNullOrWhiteSpace(summary))
        {
            db.AiSummaries.Add(new AiSummary
            {
                ChecklistRunId = run.Id,
                Kind = AiSummaryKind.RunSummary,
                Content = summary,
            });
        }

        db.ChecklistRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    public async Task<Finding> AddFindingAsync(
        NotificationSeed seed,
        Guid environmentId,
        Guid checklistItemId,
        FindingStatus status,
        DateTimeOffset firstSeenAt,
        DateTimeOffset? resolvedAt = null,
        FindingCloseReason? closeReason = null,
        Severity severity = Severity.Critical,
        string title = "P99 延迟异常",
        string? evidence = "value=900")
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        var finding = new Finding
        {
            ProjectId = seed.Project.Id,
            EnvironmentId = environmentId,
            ChecklistId = seed.Checklist.Id,
            ChecklistItemId = checklistItemId,
            DedupKey = $"{seed.Project.Id}:{environmentId}:{seed.Checklist.Id}:{checklistItemId}:{Guid.NewGuid():N}",
            Severity = severity,
            Status = status,
            CloseReason = closeReason,
            Title = title,
            Message = "测试 finding",
            Evidence = evidence,
            FirstSeenAt = firstSeenAt,
            LastSeenAt = resolvedAt ?? firstSeenAt,
            ResolvedAt = resolvedAt,
        };
        db.Findings.Add(finding);
        await db.SaveChangesAsync();
        return finding;
    }

    public async Task<List<Finding>> GetFindingsAsync() =>
        await QueryAsync(db => db.Findings.AsNoTracking().OrderBy(x => x.FirstSeenAt).ToListAsync());

    public async Task<List<LarkNotification>> GetNotificationsAsync() =>
        await QueryAsync(db => db.LarkNotifications.AsNoTracking().OrderBy(x => x.CreatedAt).ToListAsync());

    public async Task<List<AiSummary>> GetSummariesAsync() =>
        await QueryAsync(db => db.AiSummaries.AsNoTracking().OrderBy(x => x.CreatedAt).ToListAsync());

    public async Task<List<ChecklistRun>> GetRunsAsync() =>
        await QueryAsync(db => db.ChecklistRuns.AsNoTracking().OrderBy(x => x.StartedAt).ToListAsync());

    public async Task<TResult> QueryAsync<TResult>(Func<GrowthInspectorDbContext, Task<TResult>> query)
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        return await query(db);
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath))
        {
            try
            {
                File.Delete(databasePath);
            }
            catch (IOException)
            {
                // 临时库清理失败不影响测试结论。
            }
        }
    }
}
