using System.Text.Json;
using GrowthInspector.Core.Models;
using Microsoft.Extensions.AI;

namespace GrowthInspector.Agents.Tools;

/// <summary>注册到工具集的一项工具：本地工具持有 AIFunction，MCP 工具持有描述与调用入口。</summary>
public sealed record AgentToolEntry(
    string Name,
    string Description,
    bool IsLocal,
    AIFunction? Function,
    Guid? McpServerId,
    string? ServerName,
    string? McpToolName,
    Func<string, CancellationToken, Task<string>>? McpInvoker,
    AITool Declaration);

/// <summary>
/// agent 工具注册中心：统一收口工具命名、冲突检测与工具声明列表。
/// 本地工具由 AIFunctionFactory 包装；MCP 工具声明使用 server 提供的 inputSchema。
/// 同名冲突在注册期抛 <see cref="InvalidOperationException"/>，MCP 刷新时可降级为记录错误。
/// </summary>
public sealed class AgentToolRegistry
{
    private readonly Dictionary<string, AgentToolEntry> entries = new(StringComparer.Ordinal);
    private readonly List<string> registrationErrors = [];

    /// <summary>注册期记录的错误（MCP 刷新时同名冲突、非法 schema 等）。</summary>
    public IReadOnlyList<string> RegistrationErrors => registrationErrors;

    /// <summary>给 ChatOptions.Tools 用的工具声明列表。</summary>
    public IReadOnlyList<AITool> Tools => entries.Values.Select(entry => entry.Declaration).ToList();

    /// <summary>当前已注册的工具名（含前缀）。</summary>
    public IReadOnlyCollection<string> ToolNames => entries.Keys;

    public bool Contains(string name) => entries.ContainsKey(name);

    /// <summary>注册本地工具；同名冲突抛异常。</summary>
    public void AddLocal(AIFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);

        var entry = new AgentToolEntry(
            function.Name,
            function.Description ?? string.Empty,
            IsLocal: true,
            Function: function,
            McpServerId: null,
            ServerName: null,
            McpToolName: null,
            McpInvoker: null,
            Declaration: function);

        Add(entry);
    }

    /// <summary>注册 MCP 工具（工具名自动加 <c>{server}__{tool}</c> 前缀）；同名冲突抛异常。</summary>
    public void AddMcpTool(
        string serverName,
        string toolName,
        string? description,
        string? inputSchemaJson,
        Guid mcpServerId,
        Func<string, CancellationToken, Task<string>> invoker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(invoker);

        var name = BuildMcpToolName(serverName, toolName);
        var schema = ParseSchema(inputSchemaJson);
        var declaration = AIFunctionFactory.CreateDeclaration(name, description, schema, null);

        var entry = new AgentToolEntry(
            name,
            description ?? string.Empty,
            IsLocal: false,
            Function: null,
            McpServerId: mcpServerId,
            ServerName: serverName,
            McpToolName: toolName,
            McpInvoker: invoker,
            Declaration: declaration);

        Add(entry);
    }

    /// <summary>注册 MCP 工具但把冲突记录到 <see cref="RegistrationErrors"/>，不中断整批刷新。</summary>
    public bool TryAddMcpTool(
        string serverName,
        string toolName,
        string? description,
        string? inputSchemaJson,
        Guid mcpServerId,
        Func<string, CancellationToken, Task<string>> invoker,
        out string? error)
    {
        try
        {
            AddMcpTool(serverName, toolName, description, inputSchemaJson, mcpServerId, invoker);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            error = ex.Message;
            registrationErrors.Add(error);
            return false;
        }
    }

    /// <summary>记录一条注册期错误（例如某个 MCP server 拉取失败）。</summary>
    public void RecordRegistrationError(string error)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            registrationErrors.Add(error);
        }
    }

    /// <summary>移除全部 MCP 工具，保留本地工具；用于按当前 Project / Environment 刷新。</summary>
    public void RemoveMcpTools()
    {
        foreach (var name in entries.Values.Where(entry => !entry.IsLocal).Select(entry => entry.Name).ToList())
        {
            entries.Remove(name);
        }
    }

    public bool TryGet(string name, out AgentToolEntry entry) => entries.TryGetValue(name, out entry!);

    /// <summary>MCP 工具名：{server}__{tool}；非 ASCII 字母、数字、下划线、连字符替换为下划线。</summary>
    public static string BuildMcpToolName(string serverName, string toolName)
        => NormalizeSegment(serverName) + AgentToolNames.McpSeparator + NormalizeSegment(toolName);

    private void Add(AgentToolEntry entry)
    {
        if (!entries.TryAdd(entry.Name, entry))
        {
            throw new InvalidOperationException($"工具名冲突：{entry.Name} 已注册。");
        }
    }

    private static JsonElement ParseSchema(string? inputSchemaJson)
    {
        if (string.IsNullOrWhiteSpace(inputSchemaJson))
        {
            return EmptyObjectSchema();
        }

        try
        {
            using var document = JsonDocument.Parse(inputSchemaJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return EmptyObjectSchema();
            }

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return EmptyObjectSchema();
        }
    }

    private static JsonElement EmptyObjectSchema()
        => JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

    private static string NormalizeSegment(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            builder.Append(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        }

        return builder.Length == 0 ? "tool" : builder.ToString();
    }
}
