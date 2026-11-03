namespace GrowthInspector.Core.Abstractions;

/// <summary>MCP 工具描述（来自 ListToolsAsync）。</summary>
public sealed record McpToolDescriptor(string Name, string? Description, string? InputSchemaJson);

/// <summary>MCP 工具调用入口：按登记的 McpServer 拉取工具并调用。</summary>
public interface IMcpToolInvoker
{
    Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default);

    Task<string> CallToolAsync(
        Guid mcpServerId,
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken = default);
}
