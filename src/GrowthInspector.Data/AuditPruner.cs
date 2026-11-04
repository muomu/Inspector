using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Data;

/// <summary>
/// 审计留存清理：按 <see cref="InspectionOptions.AuditRetentionDays"/> 删除过期的执行历史，
/// 并显式删除挂在执行轮次下的检查结果、工具调用、AI 摘要与 Lark 留痕。
/// Finding 是当前状态而不是历史记录，不参与清理。
/// </summary>
public sealed class AuditPruner(
    IDbContextFactory<GrowthInspectorDbContext> dbContextFactory,
    IOptions<InspectionOptions> options,
    ILogger<AuditPruner> logger) : IAuditPruner
{
    /// <summary>清理一次，返回删除的记录总数（执行轮次及其级联子表之和）。</summary>
    public async Task<int> PruneAsync(CancellationToken cancellationToken = default)
    {
        var retentionDays = options.Value.AuditRetentionDays;
        if (retentionDays <= 0)
        {
            // 配置成 0 或负数时不做清理，避免把全部历史一次删光。
            logger.LogWarning("审计留存天数配置为 {RetentionDays}，必须为正数，本次跳过清理。", retentionDays);
            return 0;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var expiredRunIds = await db.ChecklistRuns
            .Where(run => run.StartedAt < cutoff)
            .Select(run => run.Id)
            .ToListAsync(cancellationToken);

        if (expiredRunIds.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            logger.LogDebug("审计清理没有发现过期执行（保留 {RetentionDays} 天，截止 {Cutoff}）。", retentionDays, cutoff);
            return 0;
        }

        // 先删子表再删执行轮次：SQLite 的级联删除可用，但显式删除能拿到每张表的删除计数，
        // 也避免只靠数据库外键行为导致漏删。
        var deletedCheckResults = await db.CheckResults
            .Where(result => expiredRunIds.Contains(result.ChecklistRunId))
            .ExecuteDeleteAsync(cancellationToken);
        var deletedToolCalls = await db.ToolCallLogs
            .Where(call => expiredRunIds.Contains(call.ChecklistRunId))
            .ExecuteDeleteAsync(cancellationToken);
        var deletedAiSummaries = await db.AiSummaries
            .Where(summary => summary.ChecklistRunId != null && expiredRunIds.Contains(summary.ChecklistRunId.Value))
            .ExecuteDeleteAsync(cancellationToken);
        var deletedLarkNotifications = await db.LarkNotifications
            .Where(notification => notification.ChecklistRunId != null && expiredRunIds.Contains(notification.ChecklistRunId.Value))
            .ExecuteDeleteAsync(cancellationToken);
        var deletedRuns = await db.ChecklistRuns
            .Where(run => expiredRunIds.Contains(run.Id))
            .ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        var total = deletedRuns + deletedCheckResults + deletedToolCalls + deletedAiSummaries + deletedLarkNotifications;
        logger.LogInformation(
            "审计清理完成：保留 {RetentionDays} 天，截止 {Cutoff}；删除执行 {Runs} 条、检查结果 {CheckResults} 条、工具调用 {ToolCalls} 条、AI 摘要 {Summaries} 条、Lark 留痕 {Notifications} 条，合计 {Total} 条。",
            retentionDays,
            cutoff,
            deletedRuns,
            deletedCheckResults,
            deletedToolCalls,
            deletedAiSummaries,
            deletedLarkNotifications,
            total);

        return total;
    }
}
