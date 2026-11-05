using GrowthInspector.Core.Models;

namespace GrowthInspector.Agents.Tools;

/// <summary>一次 agent 会话的审计上下文；本地工具与 MCP 工具都按它写 ToolCallLog。</summary>
public sealed record AgentToolCallContext(
    Guid ProjectId,
    Guid EnvironmentId,
    Guid ChecklistRunId,
    Guid? ChecklistItemId,
    AgentRole AgentRole);

/// <summary>工具调用结果：失败也返回可读文本给模型，不向 agent 循环抛异常。</summary>
public sealed record AgentToolInvocationResult(
    bool Succeeded,
    string Text,
    bool Truncated = false,
    string? ErrorMessage = null);

/// <summary>工具名前缀约定：本地工具一律 local__，MCP 工具为 {server}__{tool}。</summary>
public static class AgentToolNames
{
    public const string LocalPrefix = "local__";
    public const string McpSeparator = "__";

    public const string SearchRunbooks = LocalPrefix + "search_runbooks";
    public const string ReadRunbook = LocalPrefix + "read_runbook";
    public const string ListRunbooks = LocalPrefix + "list_runbooks";
}
