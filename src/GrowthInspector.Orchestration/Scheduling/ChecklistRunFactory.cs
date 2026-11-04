using System.Collections.Concurrent;
using System.Text.Json;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Environment = GrowthInspector.Core.Models.Environment;

namespace GrowthInspector.Orchestration.Scheduling;

/// <summary>
/// 一轮执行的创建结果。<see cref="Skipped"/> 为 true 表示同一 (检查单, 环境) 已有待执行 /
/// 执行中的轮次，本次只落一条 <see cref="RunStatus.Skipped"/> 的执行记录，不进入队列。
/// </summary>
public sealed record ChecklistRunCreation(Guid RunId, bool Skipped, string ChecklistName);

/// <summary>
/// 创建检查单执行：校验检查单 / 环境归属、按执行当时的内容固化快照、做 (检查单, 环境) 互斥判定。
/// 快照与执行记录在同一次事务里落库，保证历史解释可回溯；归档的检查单、环境、手册、检查项
/// 都不进入快照，也不允许被触发执行。
/// </summary>
public sealed class ChecklistRunFactory(
    IDbContextFactory<GrowthInspectorDbContext> dbContextFactory,
    TimeProvider timeProvider,
    ILogger<ChecklistRunFactory> logger)
{
    /// <summary>快照 JSON 的序列化约定：camelCase + 枚举按数字，与执行侧解析保持一致。</summary>
    internal static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 同一 (检查单, 环境) 的创建串行化，避免并发触发同时通过互斥检查后写出两条 Pending。
    /// </summary>
    private readonly ConcurrentDictionary<(Guid ChecklistId, Guid EnvironmentId), SemaphoreSlim> pairLocks = new();

    /// <summary>创建一轮执行。检查单 / 环境不存在、跨 Project、已归档时抛 <see cref="InvalidOperationException"/>。</summary>
    public async Task<ChecklistRunCreation> CreateAsync(
        ChecklistTrigger trigger,
        Guid checklistId,
        Guid environmentId,
        CancellationToken cancellationToken = default)
    {
        var pairLock = pairLocks.GetOrAdd((checklistId, environmentId), _ => new SemaphoreSlim(1, 1));
        await pairLock.WaitAsync(cancellationToken);
        try
        {
            return await CreateCoreAsync(trigger, checklistId, environmentId, cancellationToken);
        }
        finally
        {
            pairLock.Release();
        }
    }

    private async Task<ChecklistRunCreation> CreateCoreAsync(
        ChecklistTrigger trigger,
        Guid checklistId,
        Guid environmentId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var checklist = await db.Checklists
            .Include(x => x.Items)
            .Include(x => x.RunbookRefs)
                .ThenInclude(x => x.Runbook)
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == checklistId, cancellationToken)
            ?? throw new InvalidOperationException($"检查单 {checklistId} 不存在。");

        if (checklist.IsArchived)
        {
            throw new InvalidOperationException($"检查单「{checklist.Name}」已归档，拒绝执行。");
        }

        var environment = await db.Environments
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == environmentId, cancellationToken)
            ?? throw new InvalidOperationException($"环境 {environmentId} 不存在。");

        if (environment.IsArchived)
        {
            throw new InvalidOperationException($"环境「{environment.Name}」已归档，拒绝执行。");
        }

        if (environment.ProjectId != checklist.ProjectId)
        {
            throw new InvalidOperationException("检查单与环境不属于同一 Project，拒绝执行。");
        }

        // 通用知识：项目级手册，自动注入该项目下所有检查单（按 Project 隔离，归档不外带）。
        var generalRunbooks = await db.Runbooks
            .Where(x => x.ProjectId == checklist.ProjectId
                && x.Kind == RunbookKind.General
                && !x.IsArchived)
            .OrderBy(x => x.Title)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // 专用知识：被检查单显式引用后才注入；引用不跨 Project，归档手册不进入快照。
        var referencedRunbooks = checklist.RunbookRefs
            .Where(x => x.Runbook is { IsArchived: false, Kind: RunbookKind.Specialized })
            .Select(x => x.Runbook!)
            .OrderBy(x => x.Title)
            .ToList();

        var items = checklist.Items
            .Where(x => !x.IsArchived)
            .OrderBy(x => x.SortOrder)
            .ToList();

        var itemIds = items.Select(x => x.Id).ToList();
        var findings = itemIds.Count == 0
            ? []
            : await db.Findings
                .Where(x => itemIds.Contains(x.ChecklistItemId))
                .OrderByDescending(x => x.LastSeenAt)
                .AsNoTracking()
                .ToListAsync(cancellationToken);

        var snapshot = new ChecklistSnapshot(
            checklist.Id,
            checklist.Name,
            checklist.Frequency,
            trigger,
            items.Select(x => new ChecklistSnapshotItem(
                x.Id,
                x.Title,
                x.SortOrder,
                x.ToolName,
                x.ArgumentsTemplate,
                x.ValuePath,
                x.Operator,
                x.Threshold,
                x.Window,
                x.Severity,
                x.RuleVersion,
                x.DataSourceHint,
                x.Expectation)).ToList(),
            generalRunbooks.Select(x => new RunbookSnapshot(x.Id, x.Title, x.Kind, x.Body)).ToList(),
            referencedRunbooks.Select(x => new RunbookSnapshot(x.Id, x.Title, x.Kind, x.Body)).ToList(),
            findings.Select(x => new FindingSnapshot(
                x.ChecklistItemId,
                x.Status,
                x.RuleVersion,
                x.Title,
                x.FirstSeenAt,
                x.LastSeenAt,
                x.ResolvedAt)).ToList());

        // 互斥：Pending（已入队等待）与 Running 都算占用；不能并发跑同一 (检查单, 环境)。
        var occupied = await db.ChecklistRuns.AnyAsync(
            x => x.ChecklistId == checklistId
                && x.EnvironmentId == environmentId
                && (x.Status == RunStatus.Pending || x.Status == RunStatus.Running),
            cancellationToken);

        var now = timeProvider.GetUtcNow();
        var run = new ChecklistRun
        {
            ChecklistId = checklist.Id,
            EnvironmentId = environment.Id,
            Trigger = trigger,
            SnapshotJson = JsonSerializer.Serialize(snapshot, SnapshotJsonOptions),
            Status = occupied ? RunStatus.Skipped : RunStatus.Pending,
            StartedAt = now,
            CompletedAt = occupied ? now : null,
            ErrorMessage = occupied
                ? "同一 (检查单, 环境) 已有待执行或执行中的轮次，本次触发被跳过。"
                : null,
        };

        db.ChecklistRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);

        if (occupied)
        {
            logger.LogInformation(
                "检查单 {ChecklistId} 在环境 {EnvironmentId} 已有执行中的轮次，写入 Skipped 执行 {RunId}。",
                checklistId,
                environmentId,
                run.Id);
        }
        else
        {
            logger.LogInformation(
                "创建检查单执行 {RunId}（checklist={ChecklistId}, environment={EnvironmentId}, trigger={Trigger}，{ItemCount} 个检查项）。",
                run.Id,
                checklistId,
                environmentId,
                trigger,
                snapshot.Items.Count);
        }

        return new ChecklistRunCreation(run.Id, occupied, checklist.Name);
    }
}
