using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Data;
using GrowthInspector.Orchestration;
using GrowthInspector.Orchestration.Summaries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Environment = GrowthInspector.Core.Models.Environment;

namespace GrowthInspector.Tests;

/// <summary>TPG-2707：检查单内串行、检查单间并行、单项错误/超时继续、摘要与 finding 旁路。</summary>
public sealed class OrchestrationTests
{
    [Fact]
    public async Task RunAsync_ExecutesItemsBySortOrder_AndPersistsEachResultBeforeNextItem()
    {
        var invocations = new List<string>();
        var visibleResultCounts = new List<int>();
        InspectorTestHost? host = null;

        var executor = new DelegatingItemExecutor(async (request, cancellationToken) =>
        {
            invocations.Add(request.Item.Title);
            await using var db = await host!.DbFactory.CreateDbContextAsync(cancellationToken);
            visibleResultCounts.Add(await db.CheckResults.CountAsync(
                x => x.ChecklistRunId == request.ChecklistRunId,
                cancellationToken));

            return OkResult(request.Item.Title);
        });

        host = await InspectorTestHost.CreateAsync(
            afterBuilder: services => services.AddSingleton<IChecklistItemExecutor>(executor));

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
    }

    [Fact]
    public async Task RunAsync_WhenItemThrows_MarksErrorAndContinuesRemainingItems()
    {
        var invocations = new List<string>();
        var executor = new DelegatingItemExecutor((request, _) =>
        {
            invocations.Add(request.Item.Title);
            if (request.Item.Title == "会抛错")
            {
                throw new InvalidOperationException("工具调用炸了");
            }

            return Task.FromResult(OkResult(request.Item.Title));
        });

        await using var host = await InspectorTestHost.CreateAsync(
            afterBuilder: services => services.AddSingleton<IChecklistItemExecutor>(executor));

        var seeded = await SeedChecklistAsync(
            host,
            "exception",
            ("正常一", 0),
            ("会抛错", 1),
            ("正常二", 2));

        var orchestrator = host.Services.GetRequiredService<IInspectionOrchestrator>();
        var runId = await orchestrator.RunAsync(seeded.Checklist.Id, seeded.Environment.Id);

        Assert.Equal(["正常一", "会抛错", "正常二"], invocations);

        await using var db = await host.DbFactory.CreateDbContextAsync();
        var run = await db.ChecklistRuns.Include(x => x.Results).SingleAsync(x => x.Id == runId);
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(3, run.Results.Count);

        var failed = Assert.Single(run.Results, x => x.Status == CheckResultStatus.Error);
        Assert.Contains("工具调用炸了", failed.ErrorMessage);
        Assert.Equal(2, run.Results.Count(x => x.Status == CheckResultStatus.Ok));
    }

    [Fact]
    public async Task RunAsync_WhenItemTimesOut_MarksErrorAndContinuesRemainingItems()
    {
        var executor = new DelegatingItemExecutor(async (request, cancellationToken) =>
        {
            if (request.Item.Title == "慢检查项")
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }

            return OkResult(request.Item.Title);
        });

        await using var host = await InspectorTestHost.CreateAsync(
            afterBuilder: services => services.AddSingleton<IChecklistItemExecutor>(executor),
            configureInspector: options => options.AgentItemTimeoutSeconds = 1);

        var seeded = await SeedChecklistAsync(
            host,
            "timeout",
            ("快检查项", 0),
            ("慢检查项", 1),
            ("收尾检查项", 2));

        var orchestrator = host.Services.GetRequiredService<IInspectionOrchestrator>();
        var runId = await orchestrator.RunAsync(seeded.Checklist.Id, seeded.Environment.Id);

        await using var db = await host.DbFactory.CreateDbContextAsync();
        var run = await db.ChecklistRuns.Include(x => x.Results).SingleAsync(x => x.Id == runId);
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(3, run.Results.Count);

        var timedOut = Assert.Single(run.Results, x => x.Status == CheckResultStatus.Error);
        Assert.Contains("超时", timedOut.ErrorMessage);
        Assert.Equal(2, run.Results.Count(x => x.Status == CheckResultStatus.Ok));
    }

    [Fact]
    public async Task RunAsync_TwoChecklistsExecuteConcurrently()
    {
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var active = 0;
        var maxActive = 0;

        var executor = new DelegatingItemExecutor(async (request, cancellationToken) =>
        {
            var current = Interlocked.Increment(ref active);
            InterlockedMax(ref maxActive, current);

            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.TrySetResult();
            }

            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Interlocked.Decrement(ref active);

            return OkResult(request.Item.Title);
        });

        await using var host = await InspectorTestHost.CreateAsync(
            afterBuilder: services => services.AddSingleton<IChecklistItemExecutor>(executor));

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
    public async Task RunAsync_CallsSummaryBeforeFindingNotifier()
    {
        var calls = new List<string>();
        var summary = new RecordingSummaryService(calls);
        var notifier = new RecordingFindingNotifier(calls);
        var executor = new DelegatingItemExecutor((request, _) =>
            Task.FromResult(OkResult(request.Item.Title)));

        await using var host = await InspectorTestHost.CreateAsync(
            afterBuilder: services =>
            {
                services.AddSingleton<IChecklistItemExecutor>(executor);
                services.AddSingleton<IRunSummaryService>(summary);
                services.AddSingleton<IFindingNotifier>(notifier);
            });

        var seeded = await SeedChecklistAsync(host, "hooks", ("检查项", 0));

        var orchestrator = host.Services.GetRequiredService<IInspectionOrchestrator>();
        var runId = await orchestrator.RunAsync(seeded.Checklist.Id, seeded.Environment.Id);

        Assert.Equal([$"summary:{runId}", $"finding:{runId}"], calls);
    }

    [Fact]
    public async Task RunAsync_WhenSummaryThrows_FindingNotifierStillRunsAndRunSucceeds()
    {
        var calls = new List<string>();
        var summary = new ThrowingSummaryService();
        var notifier = new RecordingFindingNotifier(calls);
        var executor = new DelegatingItemExecutor((request, _) =>
            Task.FromResult(OkResult(request.Item.Title)));

        await using var host = await InspectorTestHost.CreateAsync(
            afterBuilder: services =>
            {
                services.AddSingleton<IChecklistItemExecutor>(executor);
                services.AddSingleton<IRunSummaryService>(summary);
                services.AddSingleton<IFindingNotifier>(notifier);
            });

        var seeded = await SeedChecklistAsync(host, "summary-fault", ("检查项", 0));

        var orchestrator = host.Services.GetRequiredService<IInspectionOrchestrator>();
        var runId = await orchestrator.RunAsync(seeded.Checklist.Id, seeded.Environment.Id);

        Assert.Equal([$"finding:{runId}"], calls);

        await using var db = await host.DbFactory.CreateDbContextAsync();
        var run = await db.ChecklistRuns.SingleAsync(x => x.Id == runId);
        Assert.Equal(RunStatus.Succeeded, run.Status);
    }

    [Fact]
    public async Task RunAsync_WhenAnotherRunIsActive_WritesSkippedRunAndDoesNotExecute()
    {
        var executed = false;
        var executor = new DelegatingItemExecutor((request, _) =>
        {
            executed = true;
            return Task.FromResult(OkResult(request.Item.Title));
        });

        await using var host = await InspectorTestHost.CreateAsync(
            afterBuilder: services => services.AddSingleton<IChecklistItemExecutor>(executor));

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
            AgentVerdict: VerdictStatus.Ok,
            FrameworkVerdict: FrameworkVerdictStatus.Ok,
            Narrative: $"narrative:{title}",
            Observations: [$"observation:{title}"]);

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
                SortOrder = item.SortOrder,
                ToolName = "mock__Query",
                ArgumentsTemplate = "{}",
                ValuePath = "$.value",
                Operator = ComparisonOperator.Eq,
                Threshold = "1",
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

    private sealed class DelegatingItemExecutor(
        Func<ChecklistItemExecutionRequest, CancellationToken, Task<ChecklistItemExecutionResult>> handler)
        : IChecklistItemExecutor
    {
        public Task<ChecklistItemExecutionResult> ExecuteAsync(
            ChecklistItemExecutionRequest request,
            CancellationToken cancellationToken = default)
            => handler(request, cancellationToken);
    }

    private sealed class RecordingSummaryService(List<string> calls) : IRunSummaryService
    {
        public Task<Guid?> SummarizeRunAsync(Guid checklistRunId, CancellationToken cancellationToken = default)
        {
            calls.Add($"summary:{checklistRunId}");
            return Task.FromResult<Guid?>(null);
        }
    }

    private sealed class ThrowingSummaryService : IRunSummaryService
    {
        public Task<Guid?> SummarizeRunAsync(Guid checklistRunId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("摘要服务故障");
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
