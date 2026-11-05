using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using Microsoft.Extensions.Logging;

namespace GrowthInspector.Agents.Execution;

/// <summary>
/// 检查单执行器的骨架实现：不调用 MCP / 模型，逐项回调「证据不足」。
/// 真实执行链路由 ChecklistRunAgentExecutor 提供，此实现仅供骨架冒烟。
/// </summary>
public sealed class StubChecklistRunAgentExecutor(ILogger<StubChecklistRunAgentExecutor> logger) : IChecklistRunAgentExecutor
{
    public async Task ExecuteAsync(
        ChecklistRunAgentRequest request,
        Func<Guid, ChecklistItemExecutionResult, Task> onItemCompleted,
        CancellationToken cancellationToken = default)
    {
        foreach (var item in request.Snapshot.Items.OrderBy(item => item.SortOrder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogDebug(
                "检查项「{Title}」执行链路未接入（骨架阶段，runId={RunId}）",
                item.Title,
                request.ChecklistRunId);

            await onItemCompleted(item.ItemId, new ChecklistItemExecutionResult(
                Status: CheckResultStatus.InsufficientEvidence,
                Evidence: "骨架阶段：agent / MCP 执行链路尚未接入。"));
        }
    }
}
