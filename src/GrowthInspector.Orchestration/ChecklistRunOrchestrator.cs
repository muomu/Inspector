using System.Diagnostics;
using System.Text.Json;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Data;
using GrowthInspector.Orchestration.Scheduling;
using GrowthInspector.Orchestration.Summaries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GrowthInspector.Orchestration;

/// <summary>
/// 检查单执行编排：读快照 → 按 SortOrder 串行执行检查项（每项结束立即落库）→ 收尾。
/// 检查单内部无并发；检查单之间的并行由后台队列 worker 提供（TPG-2707）。
/// 本类不引入 MAF，也不做检查项级 fan-out。
/// </summary>
public sealed class ChecklistRunOrchestrator(
    ChecklistRunFactory runFactory,
    IDbContextFactory<GrowthInspectorDbContext> dbContextFactory,
    IChecklistItemExecutor itemExecutor,
    IServiceProvider serviceProvider,
    TimeProvider timeProvider,
    ILogger<ChecklistRunOrchestrator> logger) : IInspectionOrchestrator, IChecklistRunExecutor
{
    /// <summary>
    /// 兼容入口：按 Manual 触发创建一轮执行并同步执行，返回执行记录 Id。
    /// 未迁移到后台队列的页面继续走这里；同一 (检查单, 环境) 已在执行时返回 Skipped 记录 Id。
    /// </summary>
    public async Task<Guid> RunAsync(
        Guid checklistId,
        Guid environmentId,
        CancellationToken cancellationToken = default)
    {
        var creation = await runFactory.CreateAsync(
            ChecklistTrigger.Manual,
            checklistId,
            environmentId,
            cancellationToken);

        if (!creation.Skipped)
        {
            await ExecuteRunAsync(creation.RunId, cancellationToken);
        }

        return creation.RunId;
    }

    /// <inheritdoc />
    public async Task ExecuteRunAsync(Guid checklistRunId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var run = await db.ChecklistRuns
            .Include(x => x.Checklist)
            .SingleOrDefaultAsync(x => x.Id == checklistRunId, cancellationToken);

        if (run is null)
        {
            logger.LogWarning("执行 {RunId} 不存在，跳过。", checklistRunId);
            return;
        }

        if (run.Status is not (RunStatus.Pending or RunStatus.Running))
        {
            logger.LogInformation(
                "执行 {RunId} 当前状态为 {Status}，不再执行（Skipped / Canceled / 已终态）。",
                checklistRunId,
                run.Status);
            return;
        }

        var checklist = run.Checklist;
        if (checklist is null)
        {
            logger.LogError("执行 {RunId} 缺少检查单关联，无法执行。", checklistRunId);
            run.Status = RunStatus.Failed;
            run.ErrorMessage = "执行记录缺少检查单关联。";
            run.CompletedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var promptTokens = 0;
        var completionTokens = 0;

        try
        {
            var snapshot = JsonSerializer.Deserialize<ChecklistSnapshot>(
                    run.SnapshotJson,
                    ChecklistRunFactory.SnapshotJsonOptions)
                ?? throw new InvalidOperationException("执行快照为空，无法开始执行。");

            var project = await db.Projects
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == checklist.ProjectId, cancellationToken)
                ?? throw new InvalidOperationException($"检查单所属 Project {checklist.ProjectId} 不存在。");

            run.Status = RunStatus.Running;
            await db.SaveChangesAsync(cancellationToken);

            foreach (var item in snapshot.Items.OrderBy(x => x.SortOrder))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var request = new ChecklistItemExecutionRequest(
                    run.Id,
                    project.Id,
                    run.EnvironmentId,
                    project.OpsContext,
                    item,
                    snapshot.GeneralRunbooks,
                    snapshot.ReferencedRunbooks,
                    snapshot.Findings.Where(x => x.ChecklistItemId == item.ItemId).ToList());

                var itemStopwatch = Stopwatch.StartNew();
                ChecklistItemExecutionResult? execution;
                try
                {
                    execution = await itemExecutor.ExecuteAsync(request, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "检查项「{Title}」执行失败（runId={RunId}），记 Error 后继续后续检查项。",
                        item.Title,
                        run.Id);
                    execution = new ChecklistItemExecutionResult(
                        Status: CheckResultStatus.Error,
                        ErrorMessage: ex.Message,
                        DurationMs: itemStopwatch.ElapsedMilliseconds);
                }

                itemStopwatch.Stop();
                execution ??= new ChecklistItemExecutionResult(
                    Status: CheckResultStatus.Error,
                    ErrorMessage: "检查项执行器返回了空结果。",
                    DurationMs: itemStopwatch.ElapsedMilliseconds);

                promptTokens += execution.PromptTokens;
                completionTokens += execution.CompletionTokens;

                // 每项结束立即落库，不攒到整轮结束（页面可在执行中逐项看到结果）。
                db.CheckResults.Add(ToCheckResult(run.Id, item.ItemId, execution, itemStopwatch.ElapsedMilliseconds));
                await db.SaveChangesAsync(cancellationToken);
            }

            // 全部检查项都产出了结果（含 Error）即视为本轮执行完成；单项错误不改变整轮成功状态。
            run.Status = RunStatus.Succeeded;
            run.ErrorMessage = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run.Status = RunStatus.Canceled;
            run.ErrorMessage = "执行被取消。";
            logger.LogInformation("执行 {RunId} 被取消。", checklistRunId);
        }
        catch (Exception ex)
        {
            run.Status = RunStatus.Failed;
            run.ErrorMessage = ex.Message;
            logger.LogError(ex, "执行 {RunId} 失败。", checklistRunId);
        }
        finally
        {
            stopwatch.Stop();
            run.CompletedAt = timeProvider.GetUtcNow();
            run.DurationMs = stopwatch.ElapsedMilliseconds;
            run.PromptTokens = promptTokens;
            run.CompletionTokens = completionTokens;

            // 收尾保存不跟随取消令牌，避免取消时丢掉最终状态。
            try
            {
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "执行 {RunId} 的终态落库失败。", checklistRunId);
            }
        }

        logger.LogInformation(
            "检查单 {ChecklistId} 在环境 {EnvironmentId} 执行结束（runId={RunId}，状态 {Status}，耗时 {ElapsedMs} ms，token {PromptTokens}+{CompletionTokens}）。",
            checklist.Id,
            run.EnvironmentId,
            run.Id,
            run.Status,
            run.DurationMs,
            run.PromptTokens,
            run.CompletionTokens);

        // 整轮结束后先生成摘要，再做 finding / Lark 判定；两者都是旁路，失败不改变执行终态，
        // 也不在这里重复实现 finding 状态机（由 TPG-2713 的 IFindingNotifier 负责）。
        if (run.Status is RunStatus.Succeeded or RunStatus.Failed)
        {
            await TrySummarizeAsync(run.Id, cancellationToken);
            await TryEvaluateFindingsAsync(run.Id, cancellationToken);
        }
    }

    private async Task TrySummarizeAsync(Guid checklistRunId, CancellationToken cancellationToken)
    {
        try
        {
            var summaryService = serviceProvider.GetService<IRunSummaryService>();
            if (summaryService is null)
            {
                logger.LogDebug("未注册 IRunSummaryService，跳过执行 {RunId} 的摘要生成。", checklistRunId);
                return;
            }

            await summaryService.SummarizeRunAsync(checklistRunId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("应用停止，跳过执行 {RunId} 的摘要生成。", checklistRunId);
        }
        catch (Exception ex)
        {
            // 摘要失败不影响本轮执行结果；AiSummary 由摘要服务自身兜底。
            logger.LogError(ex, "执行 {RunId} 的摘要生成失败（不改变执行终态）。", checklistRunId);
        }
    }

    private async Task TryEvaluateFindingsAsync(Guid checklistRunId, CancellationToken cancellationToken)
    {
        try
        {
            var findingNotifier = serviceProvider.GetService<IFindingNotifier>();
            if (findingNotifier is null)
            {
                logger.LogDebug("未注册 IFindingNotifier，跳过执行 {RunId} 的 finding 判定。", checklistRunId);
                return;
            }

            await findingNotifier.EvaluateRunAsync(checklistRunId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("应用停止，跳过执行 {RunId} 的 finding 判定。", checklistRunId);
        }
        catch (Exception ex)
        {
            // finding / Lark 失败不影响本轮执行结果。
            logger.LogError(ex, "执行 {RunId} 的 finding 判定失败（不改变执行终态）。", checklistRunId);
        }
    }

    private static CheckResult ToCheckResult(
        Guid runId,
        Guid checklistItemId,
        ChecklistItemExecutionResult execution,
        long elapsedMs)
        => new()
        {
            ChecklistRunId = runId,
            ChecklistItemId = checklistItemId,
            Status = execution.Status,
            AgentVerdict = execution.AgentVerdict,
            Narrative = execution.Narrative,
            ObservationsJson = execution.Observations is { Count: > 0 }
                ? JsonSerializer.Serialize(execution.Observations, ChecklistRunFactory.SnapshotJsonOptions)
                : null,
            ToolCallCount = execution.ToolCallCount,
            Evidence = execution.Evidence,
            DurationMs = execution.DurationMs > 0 ? execution.DurationMs : elapsedMs,
            PromptTokens = execution.PromptTokens,
            CompletionTokens = execution.CompletionTokens,
            ErrorMessage = execution.ErrorMessage,
        };
}
