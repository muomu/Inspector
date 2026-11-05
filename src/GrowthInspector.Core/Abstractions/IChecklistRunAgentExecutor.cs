using GrowthInspector.Core.Models;

namespace GrowthInspector.Core.Abstractions;

/// <summary>
/// 一轮检查单执行的请求：整轮共享一个 agent 会话（系统提示 + 项目上下文 + 手册 + 检查单概览只付一次），
/// 由执行器框架主导逐项推进。
/// </summary>
public sealed record ChecklistRunAgentRequest(
    Guid ChecklistRunId,
    Guid ProjectId,
    Guid EnvironmentId,
    string? ProjectOpsContext,
    ChecklistSnapshot Snapshot);

/// <summary>
/// 单个检查项的执行结果：agent 判定 + 证据与计量。
/// 判定由 agent 依自然语言正文给出；执行出错（LLM / MCP / 解析失败）时 Status = Error。
/// </summary>
public sealed record ChecklistItemExecutionResult(
    CheckResultStatus Status,
    string? Evidence = null,
    long DurationMs = 0,
    int PromptTokens = 0,
    int CompletionTokens = 0,
    int CachedPromptTokens = 0,
    string? ErrorMessage = null,
    VerdictStatus? AgentVerdict = null,
    string? Narrative = null,
    IReadOnlyList<string>? Observations = null,
    int ToolCallCount = 0);

/// <summary>
/// 检查单执行 agent：一轮执行一个会话，按 SortOrder 逐项取证判定；
/// 每项完成即通过 onItemCompleted 回调交回，由编排层立即落库。
/// 单项失败（LLM 异常、claim 非法）只污染该项，会话继续推进后续检查项。
/// </summary>
public interface IChecklistRunAgentExecutor
{
    Task ExecuteAsync(
        ChecklistRunAgentRequest request,
        Func<Guid, ChecklistItemExecutionResult, Task> onItemCompleted,
        CancellationToken cancellationToken = default);
}
