using System.Diagnostics;
using System.Text.Json;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Core.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Agents.Tools;

/// <summary>
/// 一次 agent 会话的工具集：持有当前角色可见的工具声明、调用计数与审计上下文。
/// 本地工具经 AIFunctionFactory 包装后由这里统一调用；MCP 工具经 IMcpToolInvoker 透传。
/// 会话内同工具同参数的调用直接复用上次成功结果（去重）；回传模型的文本按
/// ToolResultModelMaxBytes 截断，审计落库仍按 ToolResultMaxBytes 截断。
/// </summary>
public sealed class AgentToolset(
    AgentToolRegistry registry,
    ToolCallAuditor auditor,
    AgentToolCallContext context,
    IMcpToolInvoker mcpToolInvoker,
    IOptions<InspectionOptions> options,
    ILogger<AgentToolset> logger)
{
    private readonly Dictionary<string, string> resultCache = new(StringComparer.Ordinal);
    private Guid? currentItemId;

    public AgentRole Role => context.AgentRole;

    public IReadOnlyList<AITool> Tools => registry.Tools;

    public AgentToolRegistry Registry => registry;

    /// <summary>本次会话已经发起的工具调用次数（含失败与去重复用）。</summary>
    public int ToolCallCount { get; private set; }

    /// <summary>整单会话推进到某个检查项时更新审计归因；null 表示轮级（非项级）调用。</summary>
    public void SetCurrentItem(Guid? checklistItemId) => currentItemId = checklistItemId;

    public async Task<AgentToolInvocationResult> InvokeAsync(
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        ToolCallCount++;
        registry.TryGet(toolName, out var entry);

        var cacheKey = toolName + "\u0000" + CanonicalizeArguments(argumentsJson);
        if (resultCache.TryGetValue(cacheKey, out var cached))
        {
            // 同会话同工具同参数：复用上次成功结果，审计记一次零耗时命中。
            var reuse = new AgentToolInvocationResult(true, cached);
            await auditor.RecordAsync(AuditContext, toolName, entry?.McpServerId, entry?.ServerName, argumentsJson, reuse, 0, CancellationToken.None);
            return reuse;
        }

        var stopwatch = Stopwatch.StartNew();

        AgentToolInvocationResult result;
        if (entry is null)
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
            AuditContext,
            toolName,
            entry?.McpServerId,
            entry?.ServerName,
            argumentsJson,
            result,
            stopwatch.ElapsedMilliseconds,
            CancellationToken.None);

        if (result.Succeeded)
        {
            resultCache[cacheKey] = TruncateForModel(result.Text);
        }

        // 模型只见截断后的文本；审计里保留按 ToolResultMaxBytes 的完整版本。
        return result with { Text = TruncateForModel(result.Text) };
    }

    private AgentToolCallContext AuditContext => currentItemId is { } itemId
        ? context with { ChecklistItemId = itemId }
        : context;

    private string TruncateForModel(string text)
    {
        var maxBytes = Math.Max(512, options.Value.ToolResultModelMaxBytes);
        return ToolCallAuditor.Truncate(text, maxBytes).Text ?? string.Empty;
    }

    /// <summary>去重键用的参数规范化：递归按属性名排序后重新序列化，忽略键序差异。</summary>
    private static string CanonicalizeArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return CanonicalJson(document.RootElement);
        }
        catch (JsonException)
        {
            return argumentsJson;
        }
    }

    private static string CanonicalJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{"
            + string.Join(",", element.EnumerateObject()
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => JsonSerializer.Serialize(property.Name) + ":" + CanonicalJson(property.Value)))
            + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(CanonicalJson)) + "]",
        _ => element.GetRawText(),
    };

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
            // 审计由工具基座统一写入（见外层 InvokeAsync 的 ToolCallAuditor 调用）；
            // 这里不传 McpToolInvocationContext，避免 MCP client 再写一条重复的 ToolCallLog。
            var mcpResult = await mcpToolInvoker.CallToolAsync(
                entry.McpServerId!.Value,
                entry.McpToolName!,
                argumentsJson,
                context: null,
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
