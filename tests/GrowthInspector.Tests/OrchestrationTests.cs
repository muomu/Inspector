using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Data;
using GrowthInspector.Orchestration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Environment = GrowthInspector.Core.Models.Environment;

namespace GrowthInspector.Tests;

/// <summary>
/// TPG-2707：检查单内串行、检查单间并行、单项错误继续（由执行 agent 隔离）、
/// 会话级失败整轮 Failed、finding 旁路与互斥。
/// </summary>
public sealed class OrchestrationTests
{
    [Fact]
    public async Task RunAsync_ExecutesItemsBySortOrder_AndPersistsEachResultBeforeNextItem()
    {
        var invocations = new List<string>();
        var visibleResultCounts = new List<int>();
        InspectorTestHost? host = null;

        var executor = new DelegatingRunAgentExecutor(async (runId, item, cancellationToken) =>
        {
            invocations.Add(item.Title);
            await using var db = await host!.DbFactory.CreateDbContextAsync(cancellationToken);
            visibleResultCounts.Add(await db.CheckResults.CountAsync(
                x => x.ChecklistRunId == runId,
                cancellationToken));

            return OkResult(item.Title);
        });

        host = await InspectorTestHost.CreateAsync(
            afterBuilder: services => services.AddSingleton<IChecklistRunAgentExecutor>(executor));

        await using var _ = host;
        var seeded = await SeedChecklistAsync(
            host,
            "order",
            ("后执行", 2),
            ("先执行", 0),
            ("中间执行", 1));

        var orchestrator = host.Services.GetRequiredService<IInspectionOrchestrator>();
        var runId = await orchestrator.RunAsync(seeded.Checklist.Id, seeded.Environment.Id);

        Assert.Equal(["先执行", "中间执行", "后执行"], invocations);
        // 第 2、3 个检查项开始时，前一项的结果已经落库。
        Assert.Equal([0, 1, 2], visibleResultCounts);

        await using var verifyDb = await host.DbFactory.CreateDbContextAsync();
        var run = await verifyDb.ChecklistRuns
            .Include(x => x.Results)
            .SingleAsync(x => x.Id == runId);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(ChecklistTrigger.Manual, run.Trigger);
        Assert.Equal(3, run.Results.Count);
        Assert.All(run.Results, result => Assert.Equal(CheckResultStatus.Ok, result.Status));
        Assert.NotNull(run.CompletedAt);
        Assert.True(run.PromptTokens > 0);
        Assert.True(run.CompletionTokens > 0);
        Assert.Equal(run.Results.Sum(result => result.CachedPromptTokens), run.CachedPromptTokens);
    }

    [Fact]
    public async Task RunAsync_WhenItemFails_MarksErrorAndContinuesRemainingItems()
    {
        var invocations = new List<string>();
        var executor = new DelegatingRunAgentExecutor((_, item, _) =>
        {
            invocations.Add(item.Title);
            return Task.FromResult(item.Title == "会失败"
                ? ErrorResult("LLM 调用失败：工具调用炸了")
                : OkResult(item.Title));
        });

        await using var host = await InspectorTestHost.CreateAsync(
            afterBuilder: services => services.AddSingleton<IChecklistRunAgentExecutor>(executor));

        var seeded = await SeedChecklistAsync(
            host,
            "item-fault",
            ("正常一", 0),
            ("会失败", 1),
            ("正常二", 2));

        var orchestrator = host.Services.GetRequiredService<IInspectionOrchestrator>();
        var runId = await orchestrator.RunAsync(seeded.Checklist.Id, seeded.Environment.Id);

        Assert.Equal(["正常一", "会失败", "正常二"], invocations);

        await using var db = await host.DbFactory.CreateDbContextAsync();
        var run = await db.ChecklistRuns.Include(x => x.Results).SingleAsync(x => x.Id == runId);
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(3, run.Results.Count);

        var failed = Assert.Single(run.Results, x => x.Status == CheckResultStatus.Error);
        Assert.Contains("工具调用炸了", failed.ErrorMessage);
        Assert.Equal(2, run.Results.Count(x => x.Status == CheckResultStatus.Ok));
    }

    [Fact]
    public async Task RunAsync_WhenAgentSessionThrows_MarksRunFailed()
    {
        var executor = new DelegatingRunAgentExecutor((_, _, _) =>
            throw new InvalidOperationException("会话建立失败"));

        await using var host = await InspectorTestHost.CreateAsync(
            afterBuilder: services => services.AddSingleton<IChecklistRunAgentExecutor>(executor));

        var seeded = await SeedChecklistAsync(host, "session-fault", ("检查项一", 0), ("检查项二", 1));

        var orchestrator = host.Services.GetRequiredService<IInspectionOrchestrator>();
        var runId = await orchestrator.RunAsync(seeded.Checklist.Id, seeded.Environment.Id);

        await using var db = await host.DbFactory.CreateDbContextAsync();
        var run = await db.ChecklistRuns.SingleAsync(x => x.Id == runId);
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("会话建立失败", run.ErrorMessage);
    }

    [Fact]
    public async Task RunAsync_TwoChecklistsExecuteConcurrently()
    {
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var active = 0;
        var maxActive = 0;

        var executor = new DelegatingRunAgentExecutor(async (_, _, cancellationToken) =>
        {
            var current = Interlocked.Increment(ref active);
            InterlockedMax(ref maxActive, current);

            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.TrySetResult();
            }

            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Interlocked.Decrement(ref active);

            return OkResult("并行");
        });

        await using var host = await InspectorTestHost.CreateAsync(
            afterBuilder: services => services.AddSingleton<IChecklistRunAgentExecutor>(executor));

        var first = await SeedChecklistAsync(host, "parallel-a", ("A1", 0), ("A2", 1));
        var second = await SeedChecklistAsync(host, "parallel-b", ("B1", 0), ("B2", 1));

        var orchestrator = host.Services.GetRequiredService<IInspectionOrchestrator>();
        var runIds = await Task.WhenAll(
            orchestrator.RunAsync(first.Checklist.Id, first.Environment.Id),
            orchestrator.RunAsync(second.Checklist.Id, second.Environment.Id));

        Assert.True(maxActive >= 2, $"两张检查单应并行执行，实际最大并发 {maxActive}。");

        await using var db = await host.DbFactory.CreateDbContextAsync();
        var runs = await db.ChecklistRuns
            .Include(x => x.Results)
            .Where(x => runIds.Contains(x.Id))
            .ToListAsync();

        Assert.Equal(2, runs.Count);
        Assert.All(runs, run =>
        {
            Assert.Equal(RunStatus.Succeeded, run.Status);
            Assert.Equal(2, run.Results.Count);
        });
    }

    [Fact]
    public async Task RunAsync_CallsFindingNotifierAfterRun()
    {
        var calls = new List<string>();
        var notifier = new RecordingFindingNotifier(calls);
        var executor = new DelegatingRunAgentExecutor((_, item, _) =>
            Task.FromResult(OkResult(item.Title)));

        await using var host = await InspectorTestHost.CreateAsync(
            afterBuilder: services =>
            {
                services.AddSingleton<IChecklistRunAgentExecutor>(executor);
                services.AddSingleton<IFindingNotifier>(notifier);
            });

        var seeded = await SeedChecklistAsync(host, "hooks", ("检查项", 0));

        var orchestrator = host.Services.GetRequiredService<IInspectionOrchestrator>();
        var runId = await orchestrator.RunAsync(seeded.Checklist.Id, seeded.Environment.Id);

        Assert.Equal([$"finding:{runId}"], calls);
    }

    [Fact]
    public async Task RunAsync_WhenFindingNotifierThrows_RunStillSucceeds()
    {
        var executor = new DelegatingRunAgentExecutor((_, item, _) =>
            Task.FromResult(OkResult(item.Title)));

        await using var host = await InspectorTestHost.CreateAsync(
            afterBuilder: services =>
            {
                services.AddSingleton<IChecklistRunAgentExecutor>(executor);
                services.AddSingleton<IFindingNotifier>(new ThrowingFindingNotifier());
            });

        var seeded = await SeedChecklistAsync(host, "finding-fault", ("检查项", 0));

        var orchestrator = host.Services.GetRequiredService<IInspectionOrchestrator>();
        var runId = await orchestrator.RunAsync(seeded.Checklist.Id, seeded.Environment.Id);

        await using var db = await host.DbFactory.CreateDbContextAsync();
        var run = await db.ChecklistRuns.SingleAsync(x => x.Id == runId);
        Assert.Equal(RunStatus.Succeeded, run.Status);
    }

    [Fact]
    public async Task RunAsync_WhenAnotherRunIsActive_WritesSkippedRunAndDoesNotExecute()
    {
        var executed = false;
        var executor = new DelegatingRunAgentExecutor((_, item, _) =>
        {
            executed = true;
            return Task.FromResult(OkResult(item.Title));
        });

        await using var host = await InspectorTestHost.CreateAsync(
            afterBuilder: services => services.AddSingleton<IChecklistRunAgentExecutor>(executor));

        var seeded = await SeedChecklistAsync(host, "busy", ("检查项", 0));

        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            db.ChecklistRuns.Add(new ChecklistRun
            {
                ChecklistId = seeded.Checklist.Id,
                EnvironmentId = seeded.Environment.Id,
                Status = RunStatus.Running,
                SnapshotJson = "{}",
            });
            await db.SaveChangesAsync();
        }

        var orchestrator = host.Services.GetRequiredService<IInspectionOrchestrator>();
        var runId = await orchestrator.RunAsync(seeded.Checklist.Id, seeded.Environment.Id);

        Assert.False(executed);

        await using var verifyDb = await host.DbFactory.CreateDbContextAsync();
        var run = await verifyDb.ChecklistRuns.SingleAsync(x => x.Id == runId);
        Assert.Equal(RunStatus.Skipped, run.Status);
    }

    [Fact]
    public async Task ExecuteRunAsync_WhenSnapshotIsInvalid_MarksRunFailed()
    {
        await using var host = await InspectorTestHost.CreateAsync();
        var seeded = await SeedChecklistAsync(host, "bad-snapshot", ("检查项", 0));

        var run = new ChecklistRun
        {
            ChecklistId = seeded.Checklist.Id,
            EnvironmentId = seeded.Environment.Id,
            Status = RunStatus.Pending,
            SnapshotJson = "{ not a snapshot }",
        };

        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            db.ChecklistRuns.Add(run);
            await db.SaveChangesAsync();
        }

        var executor = host.Services.GetRequiredService<IChecklistRunExecutor>();
        await executor.ExecuteRunAsync(run.Id);

        await using var verifyDb = await host.DbFactory.CreateDbContextAsync();
        var persisted = await verifyDb.ChecklistRuns.SingleAsync(x => x.Id == run.Id);
        Assert.Equal(RunStatus.Failed, persisted.Status);
        Assert.False(string.IsNullOrWhiteSpace(persisted.ErrorMessage));
        Assert.NotNull(persisted.CompletedAt);
    }

    [Fact]
    public void OrchestrationAssembly_DoesNotReferenceMaf()
    {
        var references = typeof(ChecklistRunOrchestrator).Assembly
            .GetReferencedAssemblies()
            .Select(x => x.Name ?? string.Empty)
            .ToList();

        Assert.DoesNotContain(references, name =>
            name.Contains("Microsoft.Agents", StringComparison.OrdinalIgnoreCase)
            || name.Contains("MAF", StringComparison.OrdinalIgnoreCase)
            || name.Contains("AgentFramework", StringComparison.OrdinalIgnoreCase));
    }

    private static ChecklistItemExecutionResult OkResult(string title)
        => new(
            Status: CheckResultStatus.Ok,
            Evidence: $"evidence:{title}",
            DurationMs: 5,
            PromptTokens: 10,
            CompletionTokens: 3,
            CachedPromptTokens: 4,
            AgentVerdict: VerdictStatus.Ok,
            Narrative: $"narrative:{title}",
            Observations: [$"observation:{title}"]);

    private static ChecklistItemExecutionResult ErrorResult(string reason)
        => new(
            Status: CheckResultStatus.Error,
            ErrorMessage: $"未完成 LLM 判定：{reason}",
            Narrative: $"未完成 LLM 判定：{reason}");

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current)
            {
                return;
            }

            current = previous;
        }
    }

    private static async Task<SeededChecklist> SeedChecklistAsync(
        InspectorTestHost host,
        string suffix,
        params (string Title, int SortOrder)[] items)
    {
        var project = new Project { Name = $"项目-{suffix}", Code = $"code-{suffix}" };
        var environment = new Environment { Name = "prod", Project = project };
        project.Environments.Add(environment);

        var checklist = new Checklist
        {
            Name = $"检查单-{suffix}",
            Frequency = ChecklistFrequency.Manual,
            Project = project,
        };
        foreach (var item in items)
        {
            checklist.Items.Add(new ChecklistItem
            {
                Title = item.Title,
                Body = $"检查「{item.Title}」是否健康；异常先看工具返回的证据。",
                SortOrder = item.SortOrder,
                Severity = Severity.Warning,
            });
        }

        await using var db = await host.DbFactory.CreateDbContextAsync();
        db.Projects.Add(project);
        db.Checklists.Add(checklist);
        await db.SaveChangesAsync();

        return new SeededChecklist(project, environment, checklist);
    }

    private sealed record SeededChecklist(Project Project, Environment Environment, Checklist Checklist);

    /// <summary>
    /// 测试用执行 agent：按 SortOrder 逐项调 handler 并回调结果，
    /// 模拟真实 ChecklistRunAgentExecutor 的框架主导逐项推进。
    /// </summary>
    private sealed class DelegatingRunAgentExecutor(
        Func<Guid, ChecklistSnapshotItem, CancellationToken, Task<ChecklistItemExecutionResult>> handler)
        : IChecklistRunAgentExecutor
    {
        public async Task ExecuteAsync(
            ChecklistRunAgentRequest request,
            Func<Guid, ChecklistItemExecutionResult, Task> onItemCompleted,
            CancellationToken cancellationToken = default)
        {
            foreach (var item in request.Snapshot.Items.OrderBy(item => item.SortOrder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await handler(request.ChecklistRunId, item, cancellationToken);
                await onItemCompleted(item.ItemId, result);
            }
        }
    }

    private sealed class ThrowingFindingNotifier : IFindingNotifier
    {
        public Task EvaluateRunAsync(Guid checklistRunId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("finding 服务故障");
    }

    private sealed class RecordingFindingNotifier(List<string> calls) : IFindingNotifier
    {
        public Task EvaluateRunAsync(Guid checklistRunId, CancellationToken cancellationToken = default)
        {
            calls.Add($"finding:{checklistRunId}");
            return Task.CompletedTask;
        }
    }
}
