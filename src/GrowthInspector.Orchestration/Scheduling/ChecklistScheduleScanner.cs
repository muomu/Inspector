using System.Globalization;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Core.Options;
using GrowthInspector.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Orchestration.Scheduling;

/// <summary>一次调度扫描的结果。</summary>
public sealed record ScheduleScanResult(int CheckedPairs, int TriggeredPairs);

/// <summary>
/// 频率触发扫描：每次 tick 重新读库，对每个启用且未归档的 Hourly / Daily 检查单，
/// 按 Project 下未归档的 Environment 计算是否到达当前周期的触发时刻；到期就写入后台队列。
/// 同一 (检查单, 环境) 在当前周期已有 Schedule 触发的执行（含 Skipped）时不再重复触发。
/// </summary>
public sealed class ChecklistScheduleScanner(
    IDbContextFactory<GrowthInspectorDbContext> dbContextFactory,
    IChecklistRunQueue runQueue,
    IOptions<InspectionOptions> options,
    TimeProvider timeProvider,
    ILogger<ChecklistScheduleScanner> logger)
{
    public async Task<ScheduleScanResult> ScanOnceAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow();
        var timeZone = ResolveTimeZone(settings.TimeZone);
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var checklists = await db.Checklists
            .Where(x => x.IsEnabled
                && !x.IsArchived
                && (x.Frequency == ChecklistFrequency.Hourly || x.Frequency == ChecklistFrequency.Daily))
            .Select(x => new { x.Id, x.ProjectId, x.Frequency })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (checklists.Count == 0)
        {
            return new ScheduleScanResult(0, 0);
        }

        var projectIds = checklists.Select(x => x.ProjectId).Distinct().ToList();
        var environments = await db.Environments
            .Where(x => !x.IsArchived && projectIds.Contains(x.ProjectId))
            .Select(x => new { x.Id, x.ProjectId })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var checklistIds = checklists.Select(x => x.Id).ToList();
        var scheduleRuns = await db.ChecklistRuns
            .Where(x => x.Trigger == ChecklistTrigger.Schedule && checklistIds.Contains(x.ChecklistId))
            .Select(x => new { x.ChecklistId, x.EnvironmentId, x.StartedAt })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var lastScheduledRun = scheduleRuns
            .GroupBy(x => (x.ChecklistId, x.EnvironmentId))
            .ToDictionary(group => group.Key, group => group.Max(x => x.StartedAt));

        var environmentsByProject = environments
            .GroupBy(x => x.ProjectId)
            .ToDictionary(group => group.Key, group => group.Select(x => x.Id).ToList());

        var checkedPairs = 0;
        var triggeredPairs = 0;

        foreach (var checklist in checklists)
        {
            if (!environmentsByProject.TryGetValue(checklist.ProjectId, out var environmentIds))
            {
                continue;
            }

            var dueAt = ComputeDueAtLocal(checklist.Frequency, localNow, settings);
            var dueAtUtc = ToUtc(dueAt, timeZone);
            if (now < dueAtUtc)
            {
                continue;
            }

            foreach (var environmentId in environmentIds)
            {
                checkedPairs++;
                if (lastScheduledRun.TryGetValue((checklist.Id, environmentId), out var lastRun)
                    && lastRun >= dueAtUtc)
                {
                    continue;
                }

                try
                {
                    await runQueue.EnqueueAsync(
                        ChecklistTrigger.Schedule,
                        checklist.Id,
                        environmentId,
                        cancellationToken);
                    triggeredPairs++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 单个检查单 / 环境触发失败不影响同一 tick 的其他检查单；下一 tick 会重试。
                    logger.LogError(
                        ex,
                        "调度触发检查单 {ChecklistId} 在环境 {EnvironmentId} 失败。",
                        checklist.Id,
                        environmentId);
                }
            }
        }

        if (triggeredPairs > 0)
        {
            logger.LogInformation(
                "调度扫描完成：{TriggeredPairs}/{CheckedPairs} 个 (检查单, 环境) 到期并写入队列（本地时间 {LocalNow:O}）。",
                triggeredPairs,
                checkedPairs,
                localNow);
        }

        return new ScheduleScanResult(checkedPairs, triggeredPairs);
    }

    private static DateTime LocalDateTime(ChecklistFrequency frequency, DateTimeOffset localNow, InspectionOptions options)
    {
        if (frequency == ChecklistFrequency.Hourly)
        {
            var minute = Math.Clamp(options.HourlyScheduleMinute, 0, 59);
            return new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, minute, 0, DateTimeKind.Unspecified);
        }

        var dailyTime = TimeOnly.TryParse(options.DailyScheduleTime, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : new TimeOnly(10, 0);
        return new DateTime(localNow.Year, localNow.Month, localNow.Day, dailyTime.Hour, dailyTime.Minute, 0, DateTimeKind.Unspecified);
    }

    private static DateTimeOffset ComputeDueAtLocal(ChecklistFrequency frequency, DateTimeOffset localNow, InspectionOptions options)
    {
        var localDue = LocalDateTime(frequency, localNow, options);
        var offset = localNow.Offset;
        return new DateTimeOffset(localDue, offset);
    }

    private static DateTimeOffset ToUtc(DateTimeOffset dueAtLocal, TimeZoneInfo timeZone)
    {
        try
        {
            var utc = TimeZoneInfo.ConvertTimeToUtc(dueAtLocal.DateTime, timeZone);
            return new DateTimeOffset(utc, TimeSpan.Zero);
        }
        catch (ArgumentException)
        {
            // 夏令时空档 / 重叠的边界时刻（例如本地 02:30 不存在）：按本地当前偏移解释。
            return dueAtLocal.ToUniversalTime();
        }
    }

    private static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }
}
