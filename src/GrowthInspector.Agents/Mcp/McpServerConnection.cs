using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;

namespace GrowthInspector.Agents.Mcp;

/// <summary>一次 MCP 工具调用的原始结果（截断与审计由上层处理）。</summary>
public sealed record McpToolCallOutcome(bool IsError, string Text, string? ErrorMessage = null);

/// <summary>与单个 MCP server 的一条连接。真实实现包装 ModelContextProtocol 的 McpClient。</summary>
public interface IMcpServerConnection : IAsyncDisposable
{
    Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default);

    Task<McpToolCallOutcome> CallToolAsync(
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken = default);
}

/// <summary>按登记的 McpServer 建立连接；测试可替换为内存传输。</summary>
public interface IMcpServerConnectionFactory
{
    Task<IMcpServerConnection> ConnectAsync(McpServer server, CancellationToken cancellationToken = default);
}

/// <summary>MCP 连接层错误（配置缺失、参数不合法、握手失败等）。</summary>
public sealed class McpConnectionException(string message, Exception? innerException = null)
    : Exception(message, innerException);
