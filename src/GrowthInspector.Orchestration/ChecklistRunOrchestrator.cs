using System.Diagnostics;
using System.Text.Json;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GrowthInspector.Orchestration;

/// <summary>
/// 检查单执行编排：写快照 → 按 SortOrder 串行执行检查项（每项立即落库）→ 收尾。
/// 后台队列 / 并发 / 互斥 / 启动清理由 TPG-2706 接入；执行 agent（取证 + claim + 复核）
/// 由 TPG-2708 / TPG-2709 接入。本类不引入 MAF，也不做检查项级 fan-out。
/// </summary>
public sealed class ChecklistRunOrchestrator(
    IDbContextFactory<GrowthInspectorDbContext> dbContextFactory,
    IChecklistItemExecutor itemExecutor,
    ILogger<ChecklistRunOrchestrator> logger) : IInspectionOrchestrator
{
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Guid> RunAsync(Guid checklistId, Guid environmentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var checklist = await db.Checklists
            .Include(x => x.Items)
            .Include(x => x.RunbookRefs)
                .ThenInclude(x => x.Runbook)
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == checklistId, cancellationToken)
            ?? throw new InvalidOperationException($"检查单 {checklistId} 不存在。");

        var environment = await db.Environments
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == environmentId, cancellationToken)
            ?? throw new InvalidOperationException($"环境 {environmentId} 不存在。");

        if (environment.ProjectId != checklist.ProjectId)
        {
            throw new InvalidOperationException("检查单与环境不属于同一 Project，拒绝执行。");
        }

        var project = await db.Projects
            .AsNoTracking()
            .SingleAsync(x => x.Id == checklist.ProjectId, cancellationToken);

        // 通用知识：项目级手册，自动注入该项目下所有检查单（按 Project 隔离）。
        var generalRunbooks = await db.Runbooks
            .Where(x => x.ProjectId == checklist.ProjectId
                && x.Kind == RunbookKind.General
                && !x.IsArchived)
            .OrderBy(x => x.Title)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // 专用知识：被检查单显式引用后才注入；引用不跨 Project。
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
        var findings = await db.Findings
            .Where(x => itemIds.Contains(x.ChecklistItemId))
            .OrderByDescending(x => x.LastSeenAt)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var snapshot = new ChecklistSnapshot(
            checklist.Id,
            checklist.Name,
            checklist.Frequency,
            ChecklistTrigger.Manual,
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

        var run = new ChecklistRun
        {
            ChecklistId = checklist.Id,
            EnvironmentId = environment.Id,
            Trigger = ChecklistTrigger.Manual,
            SnapshotJson = JsonSerializer.Serialize(snapshot, SnapshotJsonOptions),
            Status = RunStatus.Running,
            StartedAt = DateTimeOffset.UtcNow,
        };

        db.ChecklistRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);

        var stopwatch = Stopwatch.StartNew();
        var promptTokens = 0;
        var completionTokens = 0;
        var hasError = false;

        foreach (var item in snapshot.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var request = new ChecklistItemExecutionRequest(
                run.Id,
                project.Id,
                environment.Id,
                project.OpsContext,
                item,
                snapshot.GeneralRunbooks,
                snapshot.ReferencedRunbooks,
                snapshot.Findings.Where(x => x.ChecklistItemId == item.ItemId).ToList());

            ChecklistItemExecutionResult execution;
            try
            {
                execution = await itemExecutor.ExecuteAsync(request, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "检查项「{Title}」执行失败（runId={RunId}）", item.Title, run.Id);
                hasError = true;
                execution = new ChecklistItemExecutionResult(
                    Status: CheckResultStatus.Error,
                    ErrorMessage: ex.Message);
            }

            promptTokens += execution.PromptTokens;
            completionTokens += execution.CompletionTokens;

            // 每项结束立即落库，不攒到整轮结束（AC7）。
            db.CheckResults.Add(new CheckResult
            {
                ChecklistRunId = run.Id,
                ChecklistItemId = item.ItemId,
                Status = execution.Status,
                AgentVerdict = execution.AgentVerdict,
                FrameworkVerdict = execution.FrameworkVerdict,
                RecomputedValue = execution.RecomputedValue,
                Narrative = execution.Narrative,
                ObservationsJson = execution.Observations is { Count: > 0 }
                    ? JsonSerializer.Serialize(execution.Observations, SnapshotJsonOptions)
                    : null,
                ToolCallCount = execution.ToolCallCount,
                Evidence = execution.Evidence,
                DurationMs = execution.DurationMs,
                PromptTokens = execution.PromptTokens,
                CompletionTokens = execution.CompletionTokens,
                ErrorMessage = execution.ErrorMessage,
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        stopwatch.Stop();

        run.CompletedAt = DateTimeOffset.UtcNow;
        run.DurationMs = stopwatch.ElapsedMilliseconds;
        run.PromptTokens = promptTokens;
        run.CompletionTokens = completionTokens;

        // 骨架阶段执行器固定返回 InsufficientEvidence，因此本轮统一标记为未完成执行链路，
        // 不伪造成功结论；接入真实 agent 后由 TPG-2707 / TPG-2709 决定成功与告警语义。
        run.Status = RunStatus.Failed;
        run.ErrorMessage = hasError
            ? "存在检查项执行错误，详见执行结果。"
            : "骨架阶段：agent / MCP 执行链路未接入，未产生有效判定。";

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "检查单 {ChecklistId} 在环境 {EnvironmentId} 执行完成（runId={RunId}，{ItemCount} 个检查项，耗时 {ElapsedMs} ms）。",
            checklist.Id,
            environment.Id,
            run.Id,
            snapshot.Items.Count,
            run.DurationMs);

        return run.Id;
    }
}
