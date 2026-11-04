using GrowthInspector.Core.Models;

namespace GrowthInspector.Core.Abstractions;

/// <summary>
/// 单个检查项的执行请求。执行上下文包含：该项目全部通用手册全文、被检查单引用的专用手册、
/// 当前检查项的结构化断言与 finding 历史；由执行 agent（TPG-2709）消费。
/// </summary>
public sealed record ChecklistItemExecutionRequest(
    Guid ChecklistRunId,
    Guid ProjectId,
    Guid EnvironmentId,
    string? ProjectOpsContext,
    ChecklistSnapshotItem Item,
    IReadOnlyList<RunbookSnapshot> GeneralRunbooks,
    IReadOnlyList<RunbookSnapshot> ReferencedRunbooks,
    IReadOnlyList<FindingSnapshot> FindingHistory);

/// <summary>
/// 单个检查项的执行结果：agent 判定 + 框架复核结论 + 证据与计量。
/// 框架复核不一致时 Status = Error、FrameworkVerdict = Mismatch，不产生 finding。
/// </summary>
public sealed record ChecklistItemExecutionResult(
    CheckResultStatus Status,
    string? Evidence = null,
    long DurationMs = 0,
    int PromptTokens = 0,
    int CompletionTokens = 0,
    string? ErrorMessage = null,
    VerdictStatus? AgentVerdict = null,
    FrameworkVerdictStatus? FrameworkVerdict = null,
    string? RecomputedValue = null,
    string? Narrative = null,
    IReadOnlyList<string>? Observations = null,
    int ToolCallCount = 0);

/// <summary>检查项执行器：由执行 agent（含 MCP 工具）实现；骨架阶段使用占位实现。</summary>
public interface IChecklistItemExecutor
{
    Task<ChecklistItemExecutionResult> ExecuteAsync(
        ChecklistItemExecutionRequest request,
        CancellationToken cancellationToken = default);
}
