using System.Diagnostics;
using System.Text.Json;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace GrowthInspector.Agents.Tools;

/// <summary>
/// 一次 agent 会话的工具集：持有当前角色可见的工具声明、调用计数与审计上下文。
/// 本地工具经 AIFunctionFactory 包装后由这里统一调用；MCP 工具经 IMcpToolInvoker 透传。
/// </summary>
public sealed class AgentToolset(
    AgentToolRegistry registry,
    ToolCallAuditor auditor,
    AgentToolCallContext context,
    IMcpToolInvoker mcpToolInvoker,
    ILogger<AgentToolset> logger)
{
    public AgentRole Role => context.AgentRole;

    public IReadOnlyList<AITool> Tools => registry.Tools;

    public AgentToolRegistry Registry => registry;

    /// <summary>本次会话已经发起的工具调用次数（含失败）。</summary>
    public int ToolCallCount { get; private set; }

    public async Task<AgentToolInvocationResult> InvokeAsync(
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        ToolCallCount++;
        var stopwatch = Stopwatch.StartNew();

        AgentToolEntry? entry = null;
        AgentToolInvocationResult result;
        if (!registry.TryGet(toolName, out entry))
        {
            var message = $"未知工具：{toolName}。";
            result = new AgentToolInvocationResult(false, ErrorJson(message), ErrorMessage: message);
        }
        else if (entry.IsLocal)
        {
            result = await InvokeLocalAsync(entry, argumentsJson, cancellationToken);
        }
        else
        {
            result = await InvokeMcpAsync(entry, argumentsJson, cancellationToken);
        }

        stopwatch.Stop();
        await auditor.RecordAsync(
            context,
            toolName,
            entry?.McpServerId,
            entry?.ServerName,
            argumentsJson,
            result,
            stopwatch.ElapsedMilliseconds);

        return result;
    }

    private async Task<AgentToolInvocationResult> InvokeLocalAsync(
        AgentToolEntry entry,
        string argumentsJson,
        CancellationToken cancellationToken)
    {
        try
        {
            var arguments = ParseArguments(argumentsJson);
            FillMissingArguments(entry.Function!.JsonSchema, arguments);
            var invocationResult = await entry.Function!.InvokeAsync(
                new AIFunctionArguments(arguments),
                cancellationToken);
            var text = invocationResult?.ToString() ?? "null";
            return new AgentToolInvocationResult(true, text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "本地工具 {ToolName} 调用失败。", entry.Name);
            return new AgentToolInvocationResult(false, ErrorJson(ex.Message), ErrorMessage: ex.Message);
        }
    }

    private async Task<AgentToolInvocationResult> InvokeMcpAsync(
        AgentToolEntry entry,
        string argumentsJson,
        CancellationToken cancellationToken)
    {
        try
        {
            var invocationContext = new McpToolInvocationContext(
                context.ChecklistRunId,
                context.ChecklistItemId,
                context.AgentRole);

            var mcpResult = await mcpToolInvoker.CallToolAsync(
                entry.McpServerId!.Value,
                entry.McpToolName!,
                argumentsJson,
                invocationContext,
                cancellationToken);

            if (mcpResult.Succeeded)
            {
                return new AgentToolInvocationResult(
                    true,
                    mcpResult.ResultText ?? string.Empty,
                    mcpResult.Truncated);
            }

            var message = mcpResult.ErrorMessage ?? "MCP 工具调用失败。";
            return new AgentToolInvocationResult(
                false,
                ErrorJson(message),
                mcpResult.Truncated,
                message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "MCP 工具 {ToolName} 调用失败。", entry.Name);
            return new AgentToolInvocationResult(false, ErrorJson(ex.Message), ErrorMessage: ex.Message);
        }
    }

    private static IDictionary<string, object?> ParseArguments(string argumentsJson)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return arguments;
        }

        using var document = JsonDocument.Parse(argumentsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("工具参数必须是 JSON 对象。", nameof(argumentsJson));
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            arguments[property.Name] = ConvertValue(property.Value);
        }

        return arguments;
    }

    /// <summary>
    /// AIFunction 的反射封装把无默认值的可空参数也视为必填；
    /// 工具调用省略可选参数时补 null，避免模型少传字段导致整次调用失败。
    /// </summary>
    private static void FillMissingArguments(JsonElement schema, IDictionary<string, object?> arguments)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in properties.EnumerateObject())
        {
            if (!arguments.ContainsKey(property.Name))
            {
                arguments[property.Name] = null;
            }
        }
    }

    private static object? ConvertValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => null,
        _ => value.Clone(),
    };

    private static string ErrorJson(string message)
        => JsonSerializer.Serialize(new { ok = false, error = message });
}
