using System.Diagnostics;
using System.Text.Json;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GrowthInspector.Orchestration;

/// <summary>
/// 检查单执行编排的骨架实现：写快照 → 顺序执行检查项（占位执行器）→ 落库。
/// 真正的 MAF Workflow（fan-out → fan-in → 摘要）由 TPG-2707 接入；
/// 本类保留 RunAsync 入口与快照 / 结果落库，方便 UI 与后续子任务对接。
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

        var runbooks = checklist.RunbookRefs
            .Where(x => x.Runbook is not null)
            .Select(x => new RunbookSnapshot(x.Runbook!.Id, x.Runbook!.Title, x.Runbook!.Body))
            .ToList();

        var snapshot = new ChecklistSnapshot(
            checklist.Id,
            checklist.Name,
            checklist.Frequency,
            checklist.Items
                .OrderBy(x => x.SortOrder)
                .Select(x => new ChecklistSnapshotItem(
                    x.Id,
                    x.Title,
                    x.SortOrder,
                    x.DataSourceHint,
                    x.Expectation,
                    x.Threshold,
                    x.Severity))
                .ToList(),
            runbooks);

        var run = new ChecklistRun
        {
            ChecklistId = checklist.Id,
            EnvironmentId = environment.Id,
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
                runbooks);

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

            db.CheckResults.Add(new CheckResult
            {
                ChecklistRunId = run.Id,
                ChecklistItemId = item.ItemId,
                Status = execution.Status,
                Evidence = execution.Evidence,
                DurationMs = execution.DurationMs,
                PromptTokens = execution.PromptTokens,
                CompletionTokens = execution.CompletionTokens,
                ErrorMessage = execution.ErrorMessage,
            });
        }

        stopwatch.Stop();

        run.CompletedAt = DateTimeOffset.UtcNow;
        run.DurationMs = stopwatch.ElapsedMilliseconds;
        run.PromptTokens = promptTokens;
        run.CompletionTokens = completionTokens;

        // 骨架阶段执行器固定返回 InsufficientEvidence，因此本轮统一标记为未完成执行链路，
        // 不伪造成功结论；接入真实 agent 后由 G1 / N1 决定成功与告警语义。
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
