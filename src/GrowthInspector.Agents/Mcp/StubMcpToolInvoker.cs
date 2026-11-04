using GrowthInspector.Core.Abstractions;

namespace GrowthInspector.Agents.Mcp;

/// <summary>
/// MCP 工具调用的骨架实现：工具清单为空，调用返回结构化失败。
/// 真实 client（stdio / HTTP + ListToolsAsync + 审计截断）由 TPG-2703 实现。
/// </summary>
public sealed class StubMcpToolInvoker : IMcpToolInvoker
{
    public Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(
        Guid mcpServerId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<McpToolDescriptor>>([]);

    public Task<McpToolCallResult> CallToolAsync(
        Guid mcpServerId,
        string toolName,
        string argumentsJson,
        McpToolInvocationContext? context = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new McpToolCallResult(
            Succeeded: false,
            ResultText: null,
            Truncated: false,
            DurationMs: 0,
            ErrorMessage: "MCP 工具调用尚未接入（骨架阶段，由 TPG-2703 实现）。"));
}
