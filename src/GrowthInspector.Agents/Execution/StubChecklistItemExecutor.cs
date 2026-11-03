using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using Microsoft.Extensions.Logging;

namespace GrowthInspector.Agents.Execution;

/// <summary>
/// 检查项执行器的骨架实现：不调用 MCP / 模型，直接返回「证据不足」。
/// 真实执行链路由 TPG-2708（工具基座）与 TPG-2709（执行 agent）替换。
/// </summary>
public sealed class StubChecklistItemExecutor(ILogger<StubChecklistItemExecutor> logger) : IChecklistItemExecutor
{
    public Task<ChecklistItemExecutionResult> ExecuteAsync(
        ChecklistItemExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug(
            "检查项「{Title}」执行链路未接入（骨架阶段，runId={RunId}）",
            request.Item.Title,
            request.ChecklistRunId);

        return Task.FromResult(new ChecklistItemExecutionResult(
            Status: CheckResultStatus.InsufficientEvidence,
            Evidence: "骨架阶段：agent / MCP 执行链路尚未接入（TPG-2703 / TPG-2704 / TPG-2709）。"));
    }
}
