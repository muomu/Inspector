using System.Diagnostics;
using GrowthInspector.Agents.Tools;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace GrowthInspector.Agents.Execution;

/// <summary>
/// 检查单执行 agent：一轮执行、一个会话。
/// 流程：组一次工具集 → 会话开场（system + 上下文 + 手册 + 检查单概览，整轮只付一次）→
/// 按 SortOrder 逐项注入正文推进 → 每项多轮取证 → 解析 claim → 回调交回结果。
/// 单项失败（LLM 异常 / claim 非法）只污染该项：记 Error、注入纠偏说明后继续下一项。
/// LLM 失败、无 key、claim 非法都返回 Error，不伪造结论。
/// </summary>
public sealed class ChecklistRunAgentExecutor(
    IChatClient chatClient,
    AgentToolsetFactory toolsetFactory,
    ILogger<ChecklistRunAgentExecutor> logger) : IChecklistRunAgentExecutor
{
    public async Task ExecuteAsync(
        ChecklistRunAgentRequest request,
        Func<Guid, ChecklistItemExecutionResult, Task> onItemCompleted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onItemCompleted);

        var toolset = await toolsetFactory.CreateAsync(
            new AgentToolsetRequest(
                request.ProjectId,
                request.EnvironmentId,
                request.ChecklistRunId,
                ChecklistItemId: null,
                AgentRole.Execution),
            cancellationToken);

        var items = request.Snapshot.Items.OrderBy(item => item.SortOrder).ToList();
        var messages = ExecutionPromptBuilder.BuildSessionIntro(request);

        for (var index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = items[index];
            toolset.SetCurrentItem(item.ItemId);
            var findings = request.Snapshot.Findings
                .Where(finding => finding.ChecklistItemId == item.ItemId)
                .ToList();

            messages.Add(new ChatMessage(
                ChatRole.User,
                ExecutionPromptBuilder.BuildItemPrompt(index + 1, items.Count, item, findings)));

            var stopwatch = Stopwatch.StartNew();
            var result = await ExecuteItemAsync(
                item,
                request.ChecklistRunId,
                messages,
                toolset,
                stopwatch,
                cancellationToken);

            if (result.Status == CheckResultStatus.Error)
            {
                // 纠偏说明让模型知道上一项已按 Error 收口，避免在下一项里继续纠缠。
                messages.Add(new ChatMessage(
                    ChatRole.User,
                    ExecutionPromptBuilder.BuildItemFailureNote(result.ErrorMessage ?? "未完成 LLM 判定。")));
            }

            await onItemCompleted(item.ItemId, result);
        }
    }

    private async Task<ChecklistItemExecutionResult> ExecuteItemAsync(
        ChecklistSnapshotItem item,
        Guid checklistRunId,
        List<ChatMessage> messages,
        AgentToolset toolset,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var toolCallCountBefore = toolset.ToolCallCount;
        try
        {
            var loop = await AgentToolLoop.RunAsync(
                chatClient,
                toolset,
                messages,
                maxTurns: 0,
                cancellationToken);

            if (loop.FinalText is null)
            {
                return Failure(
                    loop.ErrorMessage ?? "模型未给出最终结论。",
                    stopwatch,
                    toolset,
                    loop,
                    toolCallCountBefore);
            }

            if (!ExecutionClaimParser.TryParse(loop.FinalText, item.ItemId, out var claim, out var parseError)
                || claim is null)
            {
                return Failure(
                    $"模型输出无法解析为结构化 claim：{parseError}",
                    stopwatch,
                    toolset,
                    loop,
                    toolCallCountBefore);
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
                PromptTokens: loop.PromptTokens,
                CompletionTokens: loop.CompletionTokens,
                CachedPromptTokens: loop.CachedPromptTokens,
                AgentVerdict: claim.Verdict,
                Narrative: claim.Narrative,
                Observations: claim.Observations,
                ToolCallCount: toolset.ToolCallCount - toolCallCountBefore);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "检查项「{Title}」执行失败（runId={RunId}），记 Error 后继续后续检查项。",
                item.Title,
                checklistRunId);

            return Failure(
                $"LLM 调用失败：{ex.Message}",
                stopwatch,
                toolset,
                loop: null,
                toolCallCountBefore);
        }
    }

    private static ChecklistItemExecutionResult Failure(
        string reason,
        Stopwatch stopwatch,
        AgentToolset toolset,
        AgentLoopResult? loop,
        int toolCallCountBefore)
    {
        var narrative = reason.StartsWith("未完成 LLM 判定", StringComparison.Ordinal)
            ? reason
            : $"未完成 LLM 判定：{reason}";

        return new ChecklistItemExecutionResult(
            Status: CheckResultStatus.Error,
            DurationMs: stopwatch.ElapsedMilliseconds,
            PromptTokens: loop?.PromptTokens ?? 0,
            CompletionTokens: loop?.CompletionTokens ?? 0,
            CachedPromptTokens: loop?.CachedPromptTokens ?? 0,
            ErrorMessage: narrative,
            Narrative: narrative,
            Observations: null,
            ToolCallCount: toolset.ToolCallCount - toolCallCountBefore);
    }
}
