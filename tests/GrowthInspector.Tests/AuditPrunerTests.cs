using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Core.Options;
using GrowthInspector.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Environment = GrowthInspector.Core.Models.Environment;

namespace GrowthInspector.Tests;

/// <summary>审计留存清理：过期删除、窗口可配、级联完整、未超窗不动、finding 保留。</summary>
public sealed class AuditPrunerTests : IAsyncLifetime
{
    private readonly string databasePath =
        Path.Combine(Path.GetTempPath(), $"growth-inspector-audit-{Guid.NewGuid():N}.db");

    private ServiceProvider serviceProvider = null!;
    private IDbContextFactory<GrowthInspectorDbContext> factory = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.Configure<InspectionOptions>(_ => { });
        services.AddGrowthInspectorData(databasePath);
        serviceProvider = services.BuildServiceProvider();
        await serviceProvider.EnsureGrowthInspectorDatabaseAsync();
        factory = serviceProvider.GetRequiredService<IDbContextFactory<GrowthInspectorDbContext>>();
    }

    [Fact]
    public async Task PruneAsync_RemovesExpiredRunAndChildren_ButKeepsFinding()
    {
        var runId = await SeedRunAsync(daysAgo: 91);
        await SeedRunAsync(daysAgo: 10);

        var pruner = CreatePruner(retentionDays: 90);
        var deleted = await pruner.PruneAsync();

        Assert.True(deleted > 0);
        await using var db = await factory.CreateDbContextAsync();
        Assert.False(await db.ChecklistRuns.AnyAsync(run => run.Id == runId));
        Assert.Equal(1, await db.ChecklistRuns.CountAsync());
        Assert.Equal(1, await db.ToolCallLogs.CountAsync());
        Assert.Equal(1, await db.CheckResults.CountAsync());
        Assert.Equal(1, await db.AiSummaries.CountAsync());
        Assert.Equal(1, await db.LarkNotifications.CountAsync());
        // 过期的 finding 属于状态数据，不随历史清理删除。
        Assert.Equal(2, await db.Findings.CountAsync());
    }

    [Fact]
    public async Task PruneAsync_UsesConfiguredRetentionWindow()
    {
        var run60DaysAgo = await SeedRunAsync(daysAgo: 60);
        var run10DaysAgo = await SeedRunAsync(daysAgo: 10);

        var pruner = CreatePruner(retentionDays: 30);
        var deleted = await pruner.PruneAsync();

        Assert.True(deleted > 0);
        await using var db = await factory.CreateDbContextAsync();
        Assert.False(await db.ChecklistRuns.AnyAsync(run => run.Id == run60DaysAgo));
        Assert.True(await db.ChecklistRuns.AnyAsync(run => run.Id == run10DaysAgo));
        Assert.Equal(1, await db.CheckResults.CountAsync(result => result.ChecklistRunId == run10DaysAgo));
        Assert.Equal(1, await db.ToolCallLogs.CountAsync(call => call.ChecklistRunId == run10DaysAgo));
        Assert.Equal(1, await db.AiSummaries.CountAsync(summary => summary.ChecklistRunId == run10DaysAgo));
        Assert.Equal(1, await db.LarkNotifications.CountAsync(notification => notification.ChecklistRunId == run10DaysAgo));
    }

    [Fact]
    public async Task PruneAsync_DoesNotDeleteRecordsInsideWindow()
    {
        var runId = await SeedRunAsync(daysAgo: 10);

        var pruner = CreatePruner(retentionDays: 90);
        var deleted = await pruner.PruneAsync();

        Assert.Equal(0, deleted);
        await using var db = await factory.CreateDbContextAsync();
        Assert.True(await db.ChecklistRuns.AnyAsync(run => run.Id == runId));
        Assert.Equal(1, await db.CheckResults.CountAsync(result => result.ChecklistRunId == runId));
        Assert.Equal(1, await db.ToolCallLogs.CountAsync(call => call.ChecklistRunId == runId));
        Assert.Equal(1, await db.AiSummaries.CountAsync(summary => summary.ChecklistRunId == runId));
        Assert.Equal(1, await db.LarkNotifications.CountAsync(notification => notification.ChecklistRunId == runId));
    }

    [Fact]
    public async Task PruneAsync_SkipsWhenRetentionIsNotPositive()
    {
        var runId = await SeedRunAsync(daysAgo: 91);

        var pruner = CreatePruner(retentionDays: 0);
        var deleted = await pruner.PruneAsync();

        Assert.Equal(0, deleted);
        await using var db = await factory.CreateDbContextAsync();
        Assert.True(await db.ChecklistRuns.AnyAsync(run => run.Id == runId));
    }

    [Fact]
    public void AddGrowthInspectorAuditPruning_RegistersHostedService()
    {
        var services = new ServiceCollection();
        services.AddGrowthInspectorData(databasePath);

        services.AddGrowthInspectorAuditPruning(options =>
        {
            options.Interval = TimeSpan.FromHours(6);
            options.InitialDelay = TimeSpan.FromMinutes(1);
        });

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    private AuditPruner CreatePruner(int retentionDays)
        => new(
            factory,
            Options.Create(new InspectionOptions { AuditRetentionDays = retentionDays }),
            NullLogger<AuditPruner>.Instance);

    /// <summary>落一条历史执行，带齐检查结果 / 工具调用 / 摘要 / Lark 留痕 / finding。</summary>
    private async Task<Guid> SeedRunAsync(int daysAgo)
    {
        await using var db = await factory.CreateDbContextAsync();
        var startedAt = DateTimeOffset.UtcNow.AddDays(-daysAgo);

        var project = new Project { Name = $"项目-{Guid.NewGuid():N}", Code = Guid.NewGuid().ToString("N")[..8] };
        var environment = new Environment { Name = $"env-{Guid.NewGuid():N}", Project = project, IsDefault = true };
        var checklist = new Checklist { Name = $"检查单-{Guid.NewGuid():N}", Project = project };
        var item = new ChecklistItem { Title = $"检查项-{Guid.NewGuid():N}", Checklist = checklist };

        var run = new ChecklistRun
        {
            Checklist = checklist,
            Environment = environment,
            SnapshotJson = "{}",
            Status = RunStatus.Succeeded,
            StartedAt = startedAt,
            CompletedAt = startedAt.AddMinutes(1),
            DurationMs = 60_000,
        };
        run.Results.Add(new CheckResult
        {
            ChecklistItem = item,
            Status = CheckResultStatus.Ok,
            CreatedAt = startedAt,
        });
        run.ToolCalls.Add(new ToolCallLog
        {
            ToolName = "QueryMetric",
            Arguments = "{\"metric\":\"http_p99_latency_ms\"}",
            Result = "{\"value\":123}",
            Succeeded = true,
            DurationMs = 12,
            CreatedAt = startedAt,
        });
        db.AiSummaries.Add(new AiSummary
        {
            ChecklistRunId = run.Id,
            Kind = AiSummaryKind.RunSummary,
            Content = "历史摘要",
            CreatedAt = startedAt,
        });

        db.Projects.Add(project);
        db.Environments.Add(environment);
        db.Checklists.Add(checklist);
        db.ChecklistItems.Add(item);
        db.ChecklistRuns.Add(run);
        db.LarkNotifications.Add(new LarkNotification
        {
            ProjectId = project.Id,
            ChecklistRunId = run.Id,
            Kind = LarkNotificationKind.FindingOpened,
            PayloadJson = "{}",
            CreatedAt = startedAt,
        });
        db.Findings.Add(new Finding
        {
            ProjectId = project.Id,
            EnvironmentId = environment.Id,
            ChecklistId = checklist.Id,
            ChecklistItemId = item.Id,
            DedupKey = $"{project.Id}:{environment.Id}:{checklist.Id}:{item.Id}",
            Title = "历史异常",
            FirstSeenAt = startedAt,
            LastSeenAt = startedAt,
        });

        await db.SaveChangesAsync();
        return run.Id;
    }

    public async Task DisposeAsync()
    {
        await serviceProvider.DisposeAsync();
        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }
    }
}
