using GrowthInspector.Core.Models;

namespace GrowthInspector.Core.Abstractions;

/// <summary>单个检查项的执行请求；执行上下文包含引用手册全文 + 当前检查项 + finding 历史。</summary>
public sealed record ChecklistItemExecutionRequest(
    Guid ChecklistRunId,
    Guid ProjectId,
    Guid EnvironmentId,
    string? ProjectOpsContext,
    ChecklistSnapshotItem Item,
    IReadOnlyList<RunbookSnapshot> Runbooks);

/// <summary>单个检查项的执行结果。</summary>
public sealed record ChecklistItemExecutionResult(
    CheckResultStatus Status,
    string? Evidence = null,
    long DurationMs = 0,
    int PromptTokens = 0,
    int CompletionTokens = 0,
    string? ErrorMessage = null);

/// <summary>检查项执行器：由执行 agent（含 MCP 工具）实现；骨架阶段使用占位实现。</summary>
public interface IChecklistItemExecutor
{
    Task<ChecklistItemExecutionResult> ExecuteAsync(
        ChecklistItemExecutionRequest request,
        CancellationToken cancellationToken = default);
}
