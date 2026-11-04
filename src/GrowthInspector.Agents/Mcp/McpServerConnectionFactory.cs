using System.Text.Json;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GrowthInspector.Agents.Mcp;

/// <summary>
/// 按 <see cref="McpServer"/> 的传输配置建立 MCP 连接：
/// stdio 启动子进程，HTTP 使用 Streamable HTTP。连接生命周期由调用方管理。
/// </summary>
public sealed class McpServerConnectionFactory : IMcpServerConnectionFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly Func<McpServer, IClientTransport?>? _transportFactory;

    public McpServerConnectionFactory(ILoggerFactory loggerFactory)
        : this(loggerFactory, transportFactory: null)
    {
    }

    /// <param name="transportFactory">
    /// 测试钩子：返回非空时使用该传输，不按 McpServer 配置创建进程 / HTTP 连接。
    /// </param>
    public McpServerConnectionFactory(
        ILoggerFactory loggerFactory,
        Func<McpServer, IClientTransport?>? transportFactory)
    {
        _loggerFactory = loggerFactory;
        _transportFactory = transportFactory;
    }

    public async Task<IMcpServerConnection> ConnectAsync(
        McpServer server,
        CancellationToken cancellationToken = default)
    {
        var transport = _transportFactory?.Invoke(server) ?? CreateTransport(server);

        var client = await McpClient.CreateAsync(
            transport,
            new McpClientOptions
            {
                ClientInfo = new Implementation
                {
                    Name = "GrowthInspector",
                    Version = "1.0.0",
                },
                InitializationTimeout = TimeSpan.FromSeconds(
                    server.TimeoutSeconds > 0 ? server.TimeoutSeconds : 60),
            },
            _loggerFactory,
            cancellationToken);

        return new McpServerConnection(client);
    }

    private IClientTransport CreateTransport(McpServer server)
    {
        return server.Transport switch
        {
            McpTransport.Stdio => CreateStdioTransport(server),
            McpTransport.Http => CreateHttpTransport(server),
            _ => throw new McpConnectionException($"MCP server「{server.Name}」使用了不支持的传输类型 {server.Transport}。"),
        };
    }

    private IClientTransport CreateStdioTransport(McpServer server)
    {
        if (string.IsNullOrWhiteSpace(server.Command))
        {
            throw new McpConnectionException($"MCP server「{server.Name}」是 stdio 传输，但没有配置启动命令。");
        }

        var arguments = new List<string>();
        if (!string.IsNullOrWhiteSpace(server.ArgumentsJson))
        {
            try
            {
                arguments = JsonSerializer.Deserialize<List<string>>(server.ArgumentsJson) ?? [];
            }
            catch (JsonException exception)
            {
                throw new McpConnectionException(
                    $"MCP server「{server.Name}」的 ArgumentsJson 不是合法的字符串数组。",
                    exception);
            }
        }

        return new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Command = server.Command,
                Arguments = arguments,
                Name = server.Name,
                ShutdownTimeout = TimeSpan.FromSeconds(5),
                StandardErrorLines = line => _loggerFactory
                    .CreateLogger<McpServerConnectionFactory>()
                    .LogDebug("MCP server「{ServerName}」stderr: {Line}", server.Name, line),
            },
            _loggerFactory);
    }

    private IClientTransport CreateHttpTransport(McpServer server)
    {
        if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var endpoint))
        {
            throw new McpConnectionException($"MCP server「{server.Name}」是 HTTP 传输，但 Url 不是合法的绝对地址。");
        }

        return new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                Name = server.Name,
                ConnectionTimeout = TimeSpan.FromSeconds(
                    server.TimeoutSeconds > 0 ? server.TimeoutSeconds : 60),
            },
            _loggerFactory);
    }

    /// <summary>包装 SDK 的 McpClient，把工具清单与结果转成 Core 的契约类型。</summary>
    private sealed class McpServerConnection(McpClient client) : IMcpServerConnection
    {
        public async Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(
            CancellationToken cancellationToken = default)
        {
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            return tools
                .Select(tool => new McpToolDescriptor(
                    tool.Name,
                    tool.Description,
                    tool.ProtocolTool.InputSchema.ValueKind == JsonValueKind.Undefined
                        ? null
                        : tool.ProtocolTool.InputSchema.GetRawText()))
                .ToList();
        }

        public async Task<McpToolCallOutcome> CallToolAsync(
            string toolName,
            string argumentsJson,
            CancellationToken cancellationToken = default)
        {
            var arguments = ParseArguments(argumentsJson);
            var result = await client.CallToolAsync(
                toolName,
                arguments,
                cancellationToken: cancellationToken);

            var text = ExtractText(result);
            if (result.IsError == true)
            {
                return new McpToolCallOutcome(
                    IsError: true,
                    text,
                    string.IsNullOrWhiteSpace(text) ? $"MCP 工具「{toolName}」返回错误。" : text);
            }

            return new McpToolCallOutcome(IsError: false, text);
        }

        public ValueTask DisposeAsync() => client.DisposeAsync();

        private static Dictionary<string, object?> ParseArguments(string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson))
            {
                return [];
            }

            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson) ?? [];
            }
            catch (JsonException exception)
            {
                throw new McpConnectionException("MCP 工具参数不是合法的 JSON 对象。", exception);
            }
        }

        private static string ExtractText(CallToolResult result)
        {
            var text = result.Content is null
                ? null
                : string.Join(
                    "\n",
                    result.Content
                        .OfType<TextContentBlock>()
                        .Select(block => block.Text)
                        .Where(text => !string.IsNullOrEmpty(text)));

            if (string.IsNullOrEmpty(text) && result.StructuredContent is { } structuredContent)
            {
                text = structuredContent.GetRawText();
            }

            return text ?? string.Empty;
        }
    }
}
