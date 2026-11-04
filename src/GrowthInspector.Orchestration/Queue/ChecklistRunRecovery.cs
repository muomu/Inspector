using GrowthInspector.Core.Models;
using GrowthInspector.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GrowthInspector.Orchestration.Queue;

/// <summary>
/// 启动清理：应用重启后，上一进程遗留的 Running（进程崩溃时）与 Pending（已创建但未开始执行）
/// 都不会再有人消费，统一置为 Canceled 并写完成时间，避免它们永久占用 (检查单, 环境) 互斥位。
/// </summary>
public sealed class ChecklistRunRecovery(
    IDbContextFactory<GrowthInspectorDbContext> dbContextFactory,
    TimeProvider timeProvider,
    ILogger<ChecklistRunRecovery> logger)
{
    /// <summary>把遗留的 Running / Pending 执行置为 Canceled，返回处理的条数。</summary>
    public async Task<int> CancelLegacyRunsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var legacyRuns = await db.ChecklistRuns
            .Where(x => x.Status == RunStatus.Running || x.Status == RunStatus.Pending)
            .ToListAsync(cancellationToken);

        if (legacyRuns.Count == 0)
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow();
        foreach (var run in legacyRuns)
        {
            run.Status = RunStatus.Canceled;
            run.CompletedAt = now;
            run.DurationMs = Math.Max(0, (long)(now - run.StartedAt).TotalMilliseconds);
            run.ErrorMessage = "应用启动时清理遗留的未完成执行。";
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning("启动清理：把 {Count} 条遗留的 Running / Pending 执行置为 Canceled。", legacyRuns.Count);
        return legacyRuns.Count;
    }
}

/// <summary>在队列 worker 之前执行启动清理的 hosted service。</summary>
public sealed class ChecklistRunRecoveryHostedService(
    ChecklistRunRecovery recovery,
    ChecklistRunStartupGate startupGate,
    ILogger<ChecklistRunRecoveryHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await recovery.CancelLegacyRunsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 清理失败不应阻止应用启动；遗留执行会在后续启动时继续被清理。
            logger.LogError(ex, "启动清理遗留执行失败。");
        }
        finally
        {
            startupGate.MarkCompleted();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
