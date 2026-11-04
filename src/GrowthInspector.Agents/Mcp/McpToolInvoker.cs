using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Core.Options;
using GrowthInspector.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Agents.Mcp;

/// <summary>
/// MCP 工具调用入口：按登记的 McpServer 复用连接、把工具清单与调用结果转换成 Core 契约，
/// 并在传入 <see cref="McpToolInvocationContext"/> 时写 ToolCallLog。
/// 任何连接失败、超时、工具不存在都返回结构化失败，不向调用方抛未捕获异常。
/// </summary>
public sealed class McpToolInvoker(
    IDbContextFactory<GrowthInspectorDbContext> dbContextFactory,
    IMcpServerConnectionFactory connectionFactory,
    IOptions<InspectionOptions> inspectionOptions,
    ILogger<McpToolInvoker> logger) : IMcpToolInvoker, IAsyncDisposable
{
    /// <summary>结果入库截断的缺省上限（字节）。</summary>
    public const int DefaultMaxResultBytes = 8192;

    private const int MaxErrorMessageLength = 2000;

    private readonly ConcurrentDictionary<Guid, ConnectionEntry> connections = new();
    private readonly SemaphoreSlim connectionGate = new(1, 1);

    public async Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(
        Guid mcpServerId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var server = await FindServerAsync(mcpServerId, cancellationToken);
            if (server is null)
            {
                logger.LogWarning("MCP server 未登记，无法列出工具（Id={McpServerId}）。", mcpServerId);
                return [];
            }

            if (!server.IsEnabled || server.IsArchived)
            {
                logger.LogWarning(
                    "MCP server「{ServerName}」已禁用或归档，不列出工具（Id={McpServerId}）。",
                    server.Name,
                    mcpServerId);
                return [];
            }

            using var timeoutSource = CreateTimeoutSource(server, cancellationToken);
            var entry = await GetConnectionAsync(server, timeoutSource.Token);
            // 每次调用都向 server 重新拉取工具清单，支持 server 侧工具变更后刷新。
            var tools = await entry.Connection.ListToolsAsync(timeoutSource.Token);
            entry.Tools = tools;
            return tools;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("列出 MCP 工具已取消（Id={McpServerId}）。", mcpServerId);
            return [];
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("列出 MCP 工具超时（Id={McpServerId}）。", mcpServerId);
            await InvalidateConnectionAsync(mcpServerId);
            return [];
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "列出 MCP 工具失败（Id={McpServerId}）。", mcpServerId);
            await InvalidateConnectionAsync(mcpServerId);
            return [];
        }
    }

    public async Task<McpToolCallResult> CallToolAsync(
        Guid mcpServerId,
        string toolName,
        string argumentsJson,
        McpToolInvocationContext? context = null,
        CancellationToken cancellationToken = default)
    {
        argumentsJson ??= string.Empty;
        var stopwatch = Stopwatch.StartNew();
        McpServer? server = null;
        string? resultText = null;
        var truncated = false;
        var succeeded = false;
        string? errorMessage = null;

        try
        {
            server = await FindServerAsync(mcpServerId, cancellationToken);
            if (server is null)
            {
                errorMessage = $"MCP server 未登记（Id={mcpServerId}）。";
            }
            else if (!server.IsEnabled)
            {
                errorMessage = $"MCP server「{server.Name}」已禁用。";
            }
            else if (server.IsArchived)
            {
                errorMessage = $"MCP server「{server.Name}」已归档。";
            }
            else
            {
                using var timeoutSource = CreateTimeoutSource(server, cancellationToken);
                var entry = await GetConnectionAsync(server, timeoutSource.Token);
                var tools = entry.Tools;
                if (tools is null)
                {
                    tools = await entry.Connection.ListToolsAsync(timeoutSource.Token);
                    entry.Tools = tools;
                }

                var descriptor = FindTool(tools, toolName);
                if (descriptor is null)
                {
                    // 连接建立后 server 侧可能新增了工具，重拉一次；仍没有才判定为不存在。
                    tools = await entry.Connection.ListToolsAsync(timeoutSource.Token);
                    entry.Tools = tools;
                    descriptor = FindTool(tools, toolName);
                }

                if (descriptor is null)
                {
                    errorMessage = $"MCP server「{server.Name}」没有工具「{toolName}」。";
                }
                else
                {
                    var outcome = await entry.Connection.CallToolAsync(
                        toolName,
                        argumentsJson,
                        timeoutSource.Token);

                    var limit = ResolveMaxResultBytes(server);
                    (resultText, truncated) = TruncateUtf8(outcome.Text, limit);
                    if (outcome.IsError)
                    {
                        errorMessage = TruncateUtf8(
                            outcome.ErrorMessage ?? $"MCP 工具「{toolName}」返回错误。",
                            MaxErrorMessageLength).Text;
                    }
                    else
                    {
                        succeeded = true;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            errorMessage = "MCP 工具调用已取消。";
        }
        catch (OperationCanceledException)
        {
            errorMessage = server is null
                ? $"读取 MCP server 登记超时（Id={mcpServerId}）。"
                : $"MCP 工具调用超时（{EffectiveTimeoutSeconds(server)} 秒）。";
            if (server is not null)
            {
                await InvalidateConnectionAsync(server.Id);
            }
        }
        catch (Exception exception)
        {
            errorMessage = TruncateUtf8(exception.Message, MaxErrorMessageLength).Text;
            if (server is not null)
            {
                await InvalidateConnectionAsync(server.Id);
            }

            logger.LogWarning(
                exception,
                "MCP 工具调用失败（Id={McpServerId}, tool={ToolName}）。",
                mcpServerId,
                toolName);
        }

        stopwatch.Stop();
        var durationMs = stopwatch.ElapsedMilliseconds;

        await WriteAuditLogAsync(
            context,
            mcpServerId,
            server,
            toolName,
            argumentsJson,
            resultText,
            truncated,
            succeeded,
            durationMs,
            errorMessage);

        return new McpToolCallResult(succeeded, resultText, truncated, durationMs, errorMessage);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in connections.Values)
        {
            try
            {
                await entry.Connection.DisposeAsync();
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "释放 MCP 连接时出错（Id={McpServerId}）。", entry.ServerId);
            }
        }

        connections.Clear();
        connectionGate.Dispose();
    }

    private async Task<McpServer?> FindServerAsync(Guid mcpServerId, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.McpServers
            .AsNoTracking()
            .FirstOrDefaultAsync(server => server.Id == mcpServerId, cancellationToken);
    }

    private async Task<ConnectionEntry> GetConnectionAsync(McpServer server, CancellationToken cancellationToken)
    {
        var fingerprint = BuildFingerprint(server);
        if (connections.TryGetValue(server.Id, out var cached) && cached.Fingerprint == fingerprint)
        {
            return cached;
        }

        await connectionGate.WaitAsync(cancellationToken);
        try
        {
            if (connections.TryGetValue(server.Id, out cached) && cached.Fingerprint == fingerprint)
            {
                return cached;
            }

            var connection = await connectionFactory.ConnectAsync(server, cancellationToken);
            var entry = new ConnectionEntry(server.Id, connection, fingerprint);
            connections[server.Id] = entry;

            if (cached is not null)
            {
                try
                {
                    await cached.Connection.DisposeAsync();
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "释放旧 MCP 连接时出错（Id={McpServerId}）。", server.Id);
                }
            }

            return entry;
        }
        finally
        {
            connectionGate.Release();
        }
    }

    private async Task InvalidateConnectionAsync(Guid mcpServerId)
    {
        if (!connections.TryRemove(mcpServerId, out var entry))
        {
            return;
        }

        try
        {
            await entry.Connection.DisposeAsync();
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "释放失效 MCP 连接时出错（Id={McpServerId}）。", mcpServerId);
        }
    }

    private async Task WriteAuditLogAsync(
        McpToolInvocationContext? context,
        Guid requestedServerId,
        McpServer? server,
        string toolName,
        string argumentsJson,
        string? resultText,
        bool truncated,
        bool succeeded,
        long durationMs,
        string? errorMessage)
    {
        if (context?.ChecklistRunId is not { } checklistRunId)
        {
            // 没有执行轮次时无法满足 ToolCallLog 的外键，按无审计处理（例如 UI 的连接探测）。
            return;
        }

        var limit = ResolveMaxResultBytes(server);
        var audit = new ToolCallLog
        {
            ChecklistRunId = checklistRunId,
            ChecklistItemId = context.ChecklistItemId,
            AgentRole = context.AgentRole,
            McpServerId = server?.Id ?? requestedServerId,
            ServerName = server?.Name,
            ToolName = TruncateUtf8(toolName, 200).Text,
            Arguments = TruncateUtf8(argumentsJson, limit).Text,
            Result = resultText is null ? null : TruncateUtf8(resultText, limit).Text,
            Truncated = truncated,
            Succeeded = succeeded,
            DurationMs = durationMs,
            ErrorMessage = errorMessage is null
                ? null
                : TruncateUtf8(errorMessage, MaxErrorMessageLength).Text,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
            db.ToolCallLogs.Add(audit);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            // 审计写失败不能影响工具调用结果；抛出前只记录。
            logger.LogWarning(
                exception,
                "写入 MCP 工具调用审计失败（runId={ChecklistRunId}, tool={ToolName}）。",
                checklistRunId,
                toolName);
        }
    }

    private static McpToolDescriptor? FindTool(IReadOnlyList<McpToolDescriptor> tools, string toolName)
        => tools.FirstOrDefault(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal));

    private int ResolveMaxResultBytes(McpServer? server)
    {
        var globalLimit = inspectionOptions.Value.ToolResultMaxBytes;
        var serverLimit = server?.MaxResultBytes ?? 0;

        var limit = serverLimit > 0 && globalLimit > 0
            ? Math.Min(serverLimit, globalLimit)
            : serverLimit > 0
                ? serverLimit
                : globalLimit;

        return limit > 0 ? limit : DefaultMaxResultBytes;
    }

    private static int EffectiveTimeoutSeconds(McpServer server)
        => server.TimeoutSeconds > 0 ? server.TimeoutSeconds : 60;

    private static CancellationTokenSource CreateTimeoutSource(McpServer server, CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(EffectiveTimeoutSeconds(server)));
        return source;
    }

    private static string BuildFingerprint(McpServer server)
        => $"{server.Transport}|{server.Command}|{server.ArgumentsJson}|{server.Url}";

    /// <summary>
    /// 按 UTF-8 字节数截断，并保证不切在多字节字符中间。
    /// 返回的文本是可安全入库的前缀。
    /// </summary>
    private static (string Text, bool Truncated) TruncateUtf8(string? text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (text ?? string.Empty, false);
        }

        if (maxBytes <= 0 || Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return (text, false);
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        var length = Math.Min(maxBytes, bytes.Length);
        while (length > 0 && (bytes[length] & 0b1100_0000) == 0b1000_0000)
        {
            length--;
        }

        return (Encoding.UTF8.GetString(bytes, 0, length), true);
    }

    private sealed class ConnectionEntry(Guid serverId, IMcpServerConnection connection, string fingerprint)
    {
        public Guid ServerId { get; } = serverId;

        public IMcpServerConnection Connection { get; } = connection;

        public string Fingerprint { get; } = fingerprint;

        /// <summary>最近一次 ListToolsAsync 的结果；为空表示还需要拉取。</summary>
        public IReadOnlyList<McpToolDescriptor>? Tools { get; set; }
    }
}
