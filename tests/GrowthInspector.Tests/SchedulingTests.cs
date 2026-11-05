using System.Text.Json;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Data;
using GrowthInspector.Orchestration.Queue;
using GrowthInspector.Orchestration.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Environment = GrowthInspector.Core.Models.Environment;

namespace GrowthInspector.Tests;

/// <summary>TPG-2706：后台队列 / 频率触发 / 互斥 Skipped / 启动清理 / 快照归档过滤。</summary>
public sealed class SchedulingTests
{
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Enqueue_WhenSameChecklistAndEnvironmentRunning_WritesSkippedRun()
    {
        await using var host = await InspectorTestHost.CreateAsync();

        var project = new Project { Name = "互斥项目", Code = "mutex" };
        var environment = new Environment { Name = "prod", Project = project };
        var checklist = new Checklist { Name = "每小时检查单", Frequency = ChecklistFrequency.Hourly, Project = project };
        checklist.Items.Add(new ChecklistItem { Title = "检查项", SortOrder = 0 });

        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            db.Projects.Add(project);
            db.Environments.Add(environment);
            db.Checklists.Add(checklist);
            db.ChecklistRuns.Add(new ChecklistRun
            {
                ChecklistId = checklist.Id,
                EnvironmentId = environment.Id,
                Trigger = ChecklistTrigger.Schedule,
                Status = RunStatus.Running,
                SnapshotJson = "{}",
            });
            await db.SaveChangesAsync();
        }

        var queue = host.Services.GetRequiredService<ChecklistRunQueue>();
        var runId = await queue.EnqueueRunAsync(ChecklistTrigger.Manual, checklist.Id, environment.Id);

        await using var verifyDb = await host.DbFactory.CreateDbContextAsync();
        var runs = await verifyDb.ChecklistRuns
            .Where(x => x.ChecklistId == checklist.Id && x.EnvironmentId == environment.Id)
            .ToListAsync();

        Assert.Equal(2, runs.Count);
        var skipped = Assert.Single(runs, x => x.Id == runId);
        Assert.Equal(RunStatus.Skipped, skipped.Status);
        Assert.Equal(ChecklistTrigger.Manual, skipped.Trigger);
        Assert.NotNull(skipped.CompletedAt);
        Assert.False(string.IsNullOrWhiteSpace(skipped.ErrorMessage));

        // Skipped 轮次同样固化快照，便于解释「触发时检查单是什么样」。
        var snapshot = JsonSerializer.Deserialize<ChecklistSnapshot>(skipped.SnapshotJson, SnapshotJsonOptions);
        Assert.NotNull(snapshot);
        Assert.Single(snapshot!.Items);
    }

    [Fact]
    public async Task Enqueue_CreatesPendingRunWithSnapshot_AndFiltersArchivedContent()
    {
        await using var host = await InspectorTestHost.CreateAsync();

        var project = new Project { Name = "快照项目", Code = "snapshot" };
        var environment = new Environment { Name = "prod", Project = project };

        var generalActive = new Runbook { Title = "通用手册", Kind = RunbookKind.General, Body = "通用正文", Project = project };
        var generalArchived = new Runbook
        {
            Title = "已归档通用手册",
            Kind = RunbookKind.General,
            Body = "归档正文",
            IsArchived = true,
            Project = project,
        };
        var specializedActive = new Runbook
        {
            Title = "专用手册",
            Kind = RunbookKind.Specialized,
            Body = "专用正文",
            Project = project,
        };
        var specializedArchived = new Runbook
        {
            Title = "已归档专用手册",
            Kind = RunbookKind.Specialized,
            Body = "归档专用正文",
            IsArchived = true,
            Project = project,
        };

        var itemLater = new ChecklistItem
        {
            Title = "后执行的检查项",
            Body = "检查 P99 延迟是否低于 500ms；超过先看下游超时与连接池。",
            SortOrder = 2,
            Severity = Severity.Critical,
        };
        var itemFirst = new ChecklistItem { Title = "先执行的检查项", SortOrder = 0 };
        var itemArchived = new ChecklistItem { Title = "已归档检查项", SortOrder = 1, IsArchived = true };

        var checklist = new Checklist { Name = "快照检查单", Frequency = ChecklistFrequency.Daily, Project = project };
        checklist.Items.Add(itemLater);
        checklist.Items.Add(itemFirst);
        checklist.Items.Add(itemArchived);
        checklist.RunbookRefs.Add(new ChecklistRunbookRef { Runbook = specializedActive });
        checklist.RunbookRefs.Add(new ChecklistRunbookRef { Runbook = specializedArchived });

        var finding = new Finding
        {
            ProjectId = project.Id,
            EnvironmentId = environment.Id,
            ChecklistId = checklist.Id,
            ChecklistItemId = itemLater.Id,
            DedupKey = $"{project.Id}:{environment.Id}:{checklist.Id}:{itemLater.Id}",
            Title = "既有异常",
            Severity = Severity.Critical,
            Status = FindingStatus.Open,
        };

        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            db.Projects.Add(project);
            db.Environments.Add(environment);
            db.Runbooks.AddRange(generalActive, generalArchived, specializedActive, specializedArchived);
            db.Checklists.Add(checklist);
            db.Findings.Add(finding);
            await db.SaveChangesAsync();
        }

        var queue = host.Services.GetRequiredService<ChecklistRunQueue>();
        var runId = await queue.EnqueueRunAsync(ChecklistTrigger.Schedule, checklist.Id, environment.Id);

        await using var verifyDb = await host.DbFactory.CreateDbContextAsync();
        var run = await verifyDb.ChecklistRuns.SingleAsync(x => x.Id == runId);
        Assert.Equal(RunStatus.Pending, run.Status);
        Assert.Equal(ChecklistTrigger.Schedule, run.Trigger);

        var snapshot = JsonSerializer.Deserialize<ChecklistSnapshot>(run.SnapshotJson, SnapshotJsonOptions);
        Assert.NotNull(snapshot);

        // 检查项按 SortOrder 排序，归档项不进入快照，正文与级别原样固化。
        Assert.Equal(["先执行的检查项", "后执行的检查项"], snapshot!.Items.Select(x => x.Title));
        var later = snapshot.Items[1];
        Assert.Equal(2, later.SortOrder);
        Assert.Equal("检查 P99 延迟是否低于 500ms；超过先看下游超时与连接池。", later.Body);
        Assert.Equal(Severity.Critical, later.Severity);

        // 未归档通用手册 + 未归档被引用专用手册；归档手册不进入快照。
        Assert.Equal(["通用手册"], snapshot.GeneralRunbooks.Select(x => x.Title));
        Assert.Equal(["专用手册"], snapshot.ReferencedRunbooks.Select(x => x.Title));

        // 检查项 finding 现状随快照固化。
        var findingSnapshot = Assert.Single(snapshot.Findings);
        Assert.Equal(itemLater.Id, findingSnapshot.ChecklistItemId);
        Assert.Equal(FindingStatus.Open, findingSnapshot.Status);
    }

    [Fact]
    public async Task Recovery_CancelsLegacyRunningAndPendingRuns()
    {
        await using var host = await InspectorTestHost.CreateAsync();

        var project = new Project { Name = "清理项目", Code = "recovery" };
        var environment = new Environment { Name = "prod", Project = project };
        var checklist = new Checklist { Name = "清理检查单", Frequency = ChecklistFrequency.Manual, Project = project };
        var running = new ChecklistRun
        {
            ChecklistId = checklist.Id,
            EnvironmentId = environment.Id,
            Status = RunStatus.Running,
            SnapshotJson = "{}",
        };
        var pending = new ChecklistRun
        {
            ChecklistId = checklist.Id,
            EnvironmentId = environment.Id,
            Status = RunStatus.Pending,
            SnapshotJson = "{}",
        };
        var succeeded = new ChecklistRun
        {
            ChecklistId = checklist.Id,
            EnvironmentId = environment.Id,
            Status = RunStatus.Succeeded,
            SnapshotJson = "{}",
        };

        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            db.Projects.Add(project);
            db.Environments.Add(environment);
            db.Checklists.Add(checklist);
            db.ChecklistRuns.AddRange(running, pending, succeeded);
            await db.SaveChangesAsync();
        }

        var recovery = host.Services.GetRequiredService<ChecklistRunRecovery>();
        var affected = await recovery.CancelLegacyRunsAsync();

        Assert.Equal(2, affected);

        await using var verifyDb = await host.DbFactory.CreateDbContextAsync();
        var runs = await verifyDb.ChecklistRuns.ToDictionaryAsync(x => x.Id);
        Assert.Equal(RunStatus.Canceled, runs[running.Id].Status);
        Assert.Equal(RunStatus.Canceled, runs[pending.Id].Status);
        Assert.Equal(RunStatus.Succeeded, runs[succeeded.Id].Status);
        Assert.NotNull(runs[running.Id].CompletedAt);

        // 再执行一次不应重复处理。
        Assert.Equal(0, await recovery.CancelLegacyRunsAsync());
    }

    [Fact]
    public async Task Scheduler_TriggersDuePairsOnly_AndSkipsArchivedOrDisabled()
    {
        var fixedTime = new DateTimeOffset(2026, 1, 1, 10, 20, 0, TimeSpan.Zero);
        var recording = new RecordingRunQueue();
        var timeProvider = new FixedTimeProvider(fixedTime);

        await using var host = await InspectorTestHost.CreateAsync(
            afterBuilder: services => services.AddSingleton<IChecklistRunQueue>(recording),
            configureInspector: options =>
            {
                options.TimeZone = "UTC";
                options.HourlyScheduleMinute = 15;
                options.DailyScheduleTime = "10:00";
            },
            timeProvider: timeProvider);

        var project = new Project { Name = "调度项目", Code = "schedule" };
        var envA = new Environment { Name = "prod-a", Project = project };
        var envB = new Environment { Name = "prod-b", Project = project };
        var envArchived = new Environment { Name = "prod-archived", Project = project, IsArchived = true };

        var hourly = new Checklist { Name = "每小时检查单", Frequency = ChecklistFrequency.Hourly, Project = project };
        var daily = new Checklist { Name = "每天检查单", Frequency = ChecklistFrequency.Daily, Project = project };
        var manual = new Checklist { Name = "手动检查单", Frequency = ChecklistFrequency.Manual, Project = project };
        var disabled = new Checklist { Name = "停用检查单", Frequency = ChecklistFrequency.Hourly, IsEnabled = false, Project = project };
        var archived = new Checklist { Name = "归档检查单", Frequency = ChecklistFrequency.Hourly, IsArchived = true, Project = project };

        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            db.Projects.Add(project);
            db.Environments.AddRange(envA, envB, envArchived);
            db.Checklists.AddRange(hourly, daily, manual, disabled, archived);
            db.ChecklistRuns.AddRange(
                // hourly 在 envA 上一小时跑过（早于本小时 10:15 的触发点）→ 到期。
                NewScheduleRun(hourly, envA, new DateTimeOffset(2026, 1, 1, 9, 20, 0, TimeSpan.Zero)),
                // hourly 在 envB 本小时 10:15 已跑过 → 不重复触发。
                NewScheduleRun(hourly, envB, new DateTimeOffset(2026, 1, 1, 10, 15, 0, TimeSpan.Zero)),
                // daily 在 envA 昨天跑过 → 今天 10:00 到期。
                NewScheduleRun(daily, envA, new DateTimeOffset(2025, 12, 31, 10, 0, 0, TimeSpan.Zero)));
            await db.SaveChangesAsync();
        }

        var scanner = host.Services.GetRequiredService<ChecklistScheduleScanner>();
        var result = await scanner.ScanOnceAsync();

        // hourly: envA + envB；daily: envA + envB（envArchived 不参与）。
        Assert.Equal(4, result.CheckedPairs);
        Assert.Equal(3, result.TriggeredPairs);

        var triggered = recording.Calls
            .Select(x => (x.Trigger, x.ChecklistId, x.EnvironmentId))
            .ToHashSet();

        Assert.Equal(3, triggered.Count);
        Assert.All(recording.Calls, call => Assert.Equal(ChecklistTrigger.Schedule, call.Trigger));
        Assert.Contains((ChecklistTrigger.Schedule, hourly.Id, envA.Id), triggered);
        Assert.DoesNotContain((ChecklistTrigger.Schedule, hourly.Id, envB.Id), triggered);
        Assert.Contains((ChecklistTrigger.Schedule, daily.Id, envA.Id), triggered);
        Assert.Contains((ChecklistTrigger.Schedule, daily.Id, envB.Id), triggered);
        Assert.DoesNotContain(recording.Calls, call => call.ChecklistId == manual.Id);
        Assert.DoesNotContain(recording.Calls, call => call.ChecklistId == disabled.Id);
        Assert.DoesNotContain(recording.Calls, call => call.ChecklistId == archived.Id);
        Assert.DoesNotContain(recording.Calls, call => call.EnvironmentId == envArchived.Id);
    }

    [Fact]
    public async Task Scheduler_RereadsChecklistsEveryScan_SoEnableAndDisableTakeEffectImmediately()
    {
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 1, 1, 10, 20, 0, TimeSpan.Zero));
        var recording = new RecordingRunQueue();

        await using var host = await InspectorTestHost.CreateAsync(
            afterBuilder: services => services.AddSingleton<IChecklistRunQueue>(recording),
            configureInspector: options =>
            {
                options.TimeZone = "UTC";
                options.HourlyScheduleMinute = 15;
            },
            timeProvider: timeProvider);

        var project = new Project { Name = "即时生效项目", Code = "live-reload" };
        var environment = new Environment { Name = "prod", Project = project };
        var checklist = new Checklist
        {
            Name = "后开启的检查单",
            Frequency = ChecklistFrequency.Hourly,
            IsEnabled = false,
            Project = project,
        };

        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            db.Projects.Add(project);
            db.Environments.Add(environment);
            db.Checklists.Add(checklist);
            await db.SaveChangesAsync();
        }

        var scanner = host.Services.GetRequiredService<ChecklistScheduleScanner>();
        Assert.Equal(0, (await scanner.ScanOnceAsync()).TriggeredPairs);

        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            var entity = await db.Checklists.SingleAsync(x => x.Id == checklist.Id);
            entity.IsEnabled = true;
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, (await scanner.ScanOnceAsync()).TriggeredPairs);
        Assert.Single(recording.Calls);

        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            var entity = await db.Checklists.SingleAsync(x => x.Id == checklist.Id);
            entity.IsEnabled = false;
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, (await scanner.ScanOnceAsync()).TriggeredPairs);
        Assert.Single(recording.Calls);
    }

    [Fact]
    public async Task QueueWorker_ExecutesEnqueuedRunEndToEnd()
    {
        await using var host = await InspectorTestHost.CreateAsync();

        var project = new Project { Name = "队列端到端项目", Code = "queue-e2e" };
        var environment = new Environment { Name = "prod", Project = project };
        project.Environments.Add(environment);
        var checklist = new Checklist
        {
            Name = "队列端到端检查单",
            Frequency = ChecklistFrequency.Manual,
            Project = project,
        };
        checklist.Items.Add(new ChecklistItem { Title = "检查项一", SortOrder = 0 });
        checklist.Items.Add(new ChecklistItem { Title = "检查项二", SortOrder = 1 });

        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            db.Projects.Add(project);
            db.Checklists.Add(checklist);
            await db.SaveChangesAsync();
        }

        var hostedServices = host.Services.GetServices<IHostedService>().ToList();
        var recovery = Assert.Single(hostedServices.OfType<ChecklistRunRecoveryHostedService>());
        var worker = Assert.Single(hostedServices.OfType<ChecklistRunQueueWorker>());

        await recovery.StartAsync(CancellationToken.None);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var queue = host.Services.GetRequiredService<ChecklistRunQueue>();
            var runId = await queue.EnqueueRunAsync(
                ChecklistTrigger.Manual,
                checklist.Id,
                environment.Id);

            RunStatus? status = null;
            for (var attempt = 0; attempt < 100 && status is not (RunStatus.Succeeded or RunStatus.Failed); attempt++)
            {
                await Task.Delay(50);
                await using var db = await host.DbFactory.CreateDbContextAsync();
                status = await db.ChecklistRuns
                    .Where(x => x.Id == runId)
                    .Select(x => (RunStatus?)x.Status)
                    .SingleAsync();
            }

            Assert.Equal(RunStatus.Succeeded, status);

            // 每轮执行摘要已移除：终态落库后不再产生 AiSummary(RunSummary)。
            await using var verifyDb = await host.DbFactory.CreateDbContextAsync();
            var run = await verifyDb.ChecklistRuns
                .Include(x => x.Results)
                .SingleAsync(x => x.Id == runId);
            Assert.Equal(2, run.Results.Count);
            Assert.Equal(0, await verifyDb.AiSummaries.CountAsync(x => x.ChecklistRunId == runId));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            await recovery.StopAsync(CancellationToken.None);
        }
    }

    private static ChecklistRun NewScheduleRun(Checklist checklist, Environment environment, DateTimeOffset startedAt)
        => new()
        {
            ChecklistId = checklist.Id,
            EnvironmentId = environment.Id,
            Trigger = ChecklistTrigger.Schedule,
            Status = RunStatus.Succeeded,
            SnapshotJson = "{}",
            StartedAt = startedAt,
            CompletedAt = startedAt,
        };

    private sealed class RecordingRunQueue : IChecklistRunQueue
    {
        public List<(ChecklistTrigger Trigger, Guid ChecklistId, Guid EnvironmentId)> Calls { get; } = [];

        public ValueTask EnqueueAsync(
            ChecklistTrigger trigger,
            Guid checklistId,
            Guid environmentId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((trigger, checklistId, environmentId));
            return ValueTask.CompletedTask;
        }
    }
}
