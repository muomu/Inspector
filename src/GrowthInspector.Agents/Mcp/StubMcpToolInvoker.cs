using GrowthInspector.Core.Abstractions;

namespace GrowthInspector.Agents.Mcp;

/// <summary>
/// MCP 工具调用的骨架实现：工具清单为空，调用直接抛错。
/// 真实 client（stdio / HTTP + ListToolsAsync + 审计截断）由 TPG-2703 实现。
/// </summary>
public sealed class StubMcpToolInvoker : IMcpToolInvoker
{
    public Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(
        Guid mcpServerId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<McpToolDescriptor>>([]);

    public Task<string> CallToolAsync(
        Guid mcpServerId,
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken = default)
        => Task.FromException<string>(new NotSupportedException(
            "MCP 工具调用尚未接入（骨架阶段，由 TPG-2703 实现）。"));
}
