using GrowthInspector.Core.Models;

namespace GrowthInspector.Management;

/// <summary>
/// 管理层的校验 / 引用错误。页面直接展示 <see cref="Exception.Message"/>，
/// 管理 MCP 工具把它转成 <c>{ ok: false, error }</c>，不向调用方抛未捕获异常。
/// </summary>
public sealed class ManagementException(string message) : Exception(message);

/// <summary>分页结果：<see cref="NextCursor"/> 为空表示没有下一页。</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, string? NextCursor)
{
    public static PagedResult<T> Empty { get; } = new([], null);
}

// ---- 项目 ----

public sealed record ProjectView(
    Guid Id,
    string Name,
    string? Code,
    string? LarkWebhookUrl,
    string? OpsContext,
    bool IsArchived,
    DateTimeOffset CreatedAt);

public sealed record CreateProjectRequest(
    string Name,
    string? Code = null,
    string? LarkWebhookUrl = null,
    string? OpsContext = null);

public sealed record UpdateProjectRequest(
    string Name,
    string? Code = null,
    string? LarkWebhookUrl = null,
    string? OpsContext = null,
    bool? IsArchived = null);

// ---- 环境 ----

public sealed record EnvironmentView(
    Guid Id,
    Guid ProjectId,
    string ProjectName,
    string Name,
    bool IsDefault,
    bool IsArchived,
    DateTimeOffset CreatedAt);

public sealed record CreateEnvironmentRequest(Guid ProjectId, string Name, bool IsDefault = false);

public sealed record UpdateEnvironmentRequest(string Name, bool? IsDefault = null, bool? IsArchived = null);

// ---- MCP server ----

public sealed record McpServerView(
    Guid Id,
    Guid ProjectId,
    Guid? EnvironmentId,
    string? EnvironmentName,
    string Name,
    McpTransport Transport,
    string? Command,
    string? ArgumentsJson,
    string? Url,
    bool IsEnabled,
    bool IsArchived,
    int TimeoutSeconds,
    int MaxResultBytes,
    DateTimeOffset CreatedAt);

public sealed record CreateMcpServerRequest(
    Guid ProjectId,
    string Name,
    McpTransport Transport = McpTransport.Stdio,
    Guid? EnvironmentId = null,
    string? Command = null,
    string? ArgumentsJson = null,
    string? Url = null,
    bool IsEnabled = true,
    int TimeoutSeconds = 60,
    int MaxResultBytes = 8192);

public sealed record UpdateMcpServerRequest(
    string Name,
    McpTransport Transport,
    Guid? EnvironmentId = null,
    string? Command = null,
    string? ArgumentsJson = null,
    string? Url = null,
    bool? IsEnabled = null,
    int? TimeoutSeconds = null,
    int? MaxResultBytes = null,
    bool? IsArchived = null);

// ---- 运维手册 ----

public sealed record RunbookSummaryView(
    Guid Id,
    Guid ProjectId,
    string ProjectName,
    string Title,
    RunbookKind Kind,
    bool IsArchived,
    int ReferencedByChecklistCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record RunbookView(
    Guid Id,
    Guid ProjectId,
    string ProjectName,
    string Title,
    RunbookKind Kind,
    string Body,
    bool IsArchived,
    int ReferencedByChecklistCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateRunbookRequest(
    Guid ProjectId,
    string Title,
    string? Body = null,
    RunbookKind Kind = RunbookKind.Specialized);

public sealed record UpdateRunbookRequest(string Title, RunbookKind Kind, string Body, bool? IsArchived = null);

// ---- 检查单 ----

public sealed record ChecklistView(
    Guid Id,
    Guid ProjectId,
    string ProjectName,
    string Name,
    string? Description,
    ChecklistFrequency Frequency,
    bool IsEnabled,
    bool IsArchived,
    int ItemCount,
    int RunbookRefCount,
    DateTimeOffset? LastRunAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateChecklistRequest(
    Guid ProjectId,
    string Name,
    string? Description = null,
    ChecklistFrequency Frequency = ChecklistFrequency.Manual,
    bool IsEnabled = true);

public sealed record UpdateChecklistRequest(
    string Name,
    string? Description,
    ChecklistFrequency Frequency,
    bool IsEnabled,
    bool? IsArchived = null);

// ---- 检查项 ----

public sealed record ChecklistItemView(
    Guid Id,
    Guid ChecklistId,
    string Title,
    string Body,
    int SortOrder,
    Severity? Severity,
    bool IsArchived,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>检查项输入：标题 + 自然语言正文 + 严重级别。</summary>
public sealed record ChecklistItemInput(
    string Title,
    string Body,
    Severity? Severity = null);

public sealed record CreateChecklistItemRequest(Guid ChecklistId, ChecklistItemInput Item, int? SortOrder = null);

public sealed record UpdateChecklistItemRequest(ChecklistItemInput Item, int? SortOrder = null);

// ---- 检查单 ↔ 手册引用 ----

public sealed record ChecklistRunbookRefView(
    Guid ChecklistId,
    string ChecklistName,
    Guid RunbookId,
    string RunbookTitle,
    RunbookKind RunbookKind,
    bool RunbookIsArchived);

// ---- 执行 / 结果 / 异常 / 摘要 / 工具调用 ----

public sealed record ChecklistRunView(
    Guid Id,
    Guid ChecklistId,
    string ChecklistName,
    Guid EnvironmentId,
    string EnvironmentName,
    ChecklistTrigger Trigger,
    RunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    long DurationMs,
    int PromptTokens,
    int CompletionTokens,
    int ResultCount,
    string? ErrorMessage);

/// <summary>检查项结果：agent 判定、证据与计量，供管理 MCP 只读核对。</summary>
public sealed record CheckResultView(
    Guid Id,
    Guid ChecklistRunId,
    Guid ChecklistItemId,
    string ChecklistItemTitle,
    CheckResultStatus Status,
    VerdictStatus? AgentVerdict,
    string? Narrative,
    string? Evidence,
    string? ObservationsJson,
    int ToolCallCount,
    long DurationMs,
    int PromptTokens,
    int CompletionTokens,
    string? ErrorMessage,
    DateTimeOffset CreatedAt);

public sealed record FindingView(
    Guid Id,
    Guid ProjectId,
    Guid EnvironmentId,
    Guid ChecklistId,
    Guid ChecklistItemId,
    string Title,
    string? Message,
    Severity Severity,
    FindingStatus Status,
    FindingCloseReason? CloseReason,
    string? Evidence,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? ResolvedAt);

public sealed record AiSummaryView(
    Guid Id,
    AiSummaryKind Kind,
    Guid? ChecklistRunId,
    DateOnly? ReportDate,
    string Content,
    string? Model,
    bool FallbackUsed,
    int PromptTokens,
    int CompletionTokens,
    long DurationMs,
    DateTimeOffset CreatedAt);

public sealed record ToolCallLogView(
    Guid Id,
    Guid ChecklistRunId,
    Guid? ChecklistItemId,
    AgentRole? AgentRole,
    Guid? McpServerId,
    string? ServerName,
    string ToolName,
    string? Arguments,
    string? Result,
    bool Truncated,
    bool Succeeded,
    long DurationMs,
    string? ErrorMessage,
    DateTimeOffset CreatedAt);

public sealed record ChecklistRunEnqueuedResult(
    Guid ChecklistId,
    Guid EnvironmentId,
    ChecklistTrigger Trigger);
