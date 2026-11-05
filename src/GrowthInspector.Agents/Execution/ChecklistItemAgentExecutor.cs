using System.Diagnostics;
using System.Text.Json;
using GrowthInspector.Agents.Tools;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Core.Options;
using GrowthInspector.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Agents.Execution;

/// <summary>
/// 检查项执行 agent：每次执行一个检查项、一个会话。
/// 流程：组工具集 → 注入项目上下文 / 手册 / 自然语言正文 / finding 历史 → 多轮取证 → 解析 claim。
/// LLM 失败、超时、无 key 或 claim 非法都返回 Error，不伪造结论；
/// 判定完全由 agent 依检查项正文给出（框架不再按结构化断言复核）。
/// </summary>
public sealed class ChecklistItemAgentExecutor(
    IChatClient chatClient,
    AgentToolsetFactory toolsetFactory,
    IDbContextFactory<GrowthInspectorDbContext> dbContextFactory,
    IOptions<InspectionOptions> options,
    ILogger<ChecklistItemAgentExecutor> logger) : IChecklistItemExecutor
{
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ChecklistItemExecutionResult> ExecuteAsync(
        ChecklistItemExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();
        var promptTokens = 0;
        var completionTokens = 0;
        AgentToolset? toolset = null;

        var timeoutSeconds = Math.Max(1, options.Value.AgentItemTimeoutSeconds);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            toolset = await toolsetFactory.CreateAsync(
                new AgentToolsetRequest(
                    request.ProjectId,
                    request.EnvironmentId,
                    request.ChecklistRunId,
                    request.Item.ItemId,
                    AgentRole.Execution),
                timeoutCts.Token);

            var snapshot = await LoadSnapshotAsync(request.ChecklistRunId, timeoutCts.Token);
            var messages = ExecutionPromptBuilder.Build(request, snapshot);

            var loop = await AgentToolLoop.RunAsync(
                chatClient,
                toolset,
                messages,
                options.Value.AgentMaxTurns,
                timeoutCts.Token);

            promptTokens = loop.PromptTokens;
            completionTokens = loop.CompletionTokens;

            if (loop.FinalText is null)
            {
                return Failure(
                    loop.ErrorMessage ?? "模型未给出最终结论。",
                    stopwatch,
                    toolset,
                    promptTokens,
                    completionTokens);
            }

            if (!ExecutionClaimParser.TryParse(loop.FinalText, request.Item.ItemId, out var claim, out var parseError)
                || claim is null)
            {
                return Failure(
                    $"模型输出无法解析为结构化 claim：{parseError}",
                    stopwatch,
                    toolset,
                    promptTokens,
                    completionTokens);
            }

            var status = claim.Verdict switch
            {
                VerdictStatus.Ok => CheckResultStatus.Ok,
                VerdictStatus.Abnormal => CheckResultStatus.Abnormal,
                _ => CheckResultStatus.InsufficientEvidence,
            };

            return new ChecklistItemExecutionResult(
                Status: status,
                Evidence: claim.Evidence,
                DurationMs: stopwatch.ElapsedMilliseconds,
                PromptTokens: promptTokens,
                CompletionTokens: completionTokens,
                AgentVerdict: claim.Verdict,
                Narrative: claim.Narrative,
                Observations: claim.Observations,
                ToolCallCount: toolset.ToolCallCount);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "检查项「{Title}」执行超时（{TimeoutSeconds} 秒，runId={RunId}）。",
                request.Item.Title,
                timeoutSeconds,
                request.ChecklistRunId);

            return Failure(
                $"执行超时（{timeoutSeconds} 秒）。",
                stopwatch,
                toolset,
                promptTokens,
                completionTokens);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "检查项「{Title}」执行失败（runId={RunId}）。",
                request.Item.Title,
                request.ChecklistRunId);

            return Failure(
                $"LLM 调用失败：{ex.Message}",
                stopwatch,
                toolset,
                promptTokens,
                completionTokens);
        }
    }

    private async Task<ChecklistSnapshot?> LoadSnapshotAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var snapshotJson = await db.ChecklistRuns
            .AsNoTracking()
            .Where(run => run.Id == runId)
            .Select(run => run.SnapshotJson)
            .SingleOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(snapshotJson))
        {
            return null;
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<ChecklistSnapshot>(snapshotJson, SnapshotJsonOptions);
            if (snapshot?.Items is null
                || snapshot.GeneralRunbooks is null
                || snapshot.ReferencedRunbooks is null
                || snapshot.Findings is null)
            {
                logger.LogWarning("检查单快照缺少必要字段（runId={RunId}），本次只注入当前检查项。", runId);
                return null;
            }

            return snapshot;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "检查单快照解析失败（runId={RunId}），本次只注入当前检查项。", runId);
            return null;
        }
    }

    private static ChecklistItemExecutionResult Failure(
        string reason,
        Stopwatch stopwatch,
        AgentToolset? toolset,
        int promptTokens,
        int completionTokens)
    {
        var narrative = reason.StartsWith("未完成 LLM 判定", StringComparison.Ordinal)
            ? reason
            : $"未完成 LLM 判定：{reason}";

        return new ChecklistItemExecutionResult(
            Status: CheckResultStatus.Error,
            DurationMs: stopwatch.ElapsedMilliseconds,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            ErrorMessage: narrative,
            Narrative: narrative,
            Observations: null,
            ToolCallCount: toolset?.ToolCallCount ?? 0);
    }
}
