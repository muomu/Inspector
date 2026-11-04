using GrowthInspector.Core.Models;

namespace GrowthInspector.Core.Abstractions;

/// <summary>MCP 工具描述（来自 ListToolsAsync）。</summary>
public sealed record McpToolDescriptor(string Name, string? Description, string? InputSchemaJson);

/// <summary>
/// 一次工具调用的审计上下文：调用方（agent 工具基座）提供，MCP client 据此写入 ToolCallLog。
/// 为空时不写审计（例如 UI 的连接探测）。
/// </summary>
public sealed record McpToolInvocationContext(
    Guid? ChecklistRunId = null,
    Guid? ChecklistItemId = null,
    AgentRole? AgentRole = null);

/// <summary>MCP 工具调用结果：成功 / 失败都返回结构化对象，不向调用方抛未捕获异常。</summary>
public sealed record McpToolCallResult(
    bool Succeeded,
    string? ResultText,
    bool Truncated,
    long DurationMs,
    string? ErrorMessage = null);

/// <summary>MCP 工具调用入口：按登记的 McpServer 拉取工具并调用。</summary>
public interface IMcpToolInvoker
{
    Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default);

    Task<McpToolCallResult> CallToolAsync(
        Guid mcpServerId,
        string toolName,
        string argumentsJson,
        McpToolInvocationContext? context = null,
        CancellationToken cancellationToken = default);
}
