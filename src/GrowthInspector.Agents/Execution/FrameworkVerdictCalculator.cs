using System.Globalization;
using System.Text.Json;
using GrowthInspector.Core.Models;

namespace GrowthInspector.Agents.Execution;

/// <summary>框架复核结论：判定、重算取值与失败原因。</summary>
public sealed record FrameworkVerdictResult(
    FrameworkVerdictStatus Verdict,
    string? RecomputedValue,
    string? ErrorMessage);

/// <summary>
/// 框架复核：从本项本轮 ToolCallLog（AgentRole=Execution）的成功结果 JSON 中取值，
/// 按检查项声明的 Operator / Threshold 重算断言是否成立。
/// 判据语义：结构化断言描述健康状态，满足 = Ok，不满足 = Abnormal。
/// </summary>
public static class FrameworkVerdictCalculator
{
    public static FrameworkVerdictResult Compute(
        ChecklistSnapshotItem item,
        IReadOnlyList<ToolCallLog> toolCallLogs)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(toolCallLogs);

        if (item.Operator is null)
        {
            // 信息型检查项没有结构化断言，框架不做重算。
            return new FrameworkVerdictResult(FrameworkVerdictStatus.InsufficientEvidence, null, null);
        }

        if (string.IsNullOrWhiteSpace(item.ToolName))
        {
            return Error("检查项未声明工具名，无法复核。");
        }

        if (string.IsNullOrWhiteSpace(item.Threshold))
        {
            return Error("检查项未声明阈值，无法复核。");
        }

        var candidate = toolCallLogs
            .Where(log => log.AgentRole == AgentRole.Execution
                && log.Succeeded
                && string.Equals(log.ToolName, item.ToolName, StringComparison.Ordinal))
            // 同一轮里多个检查项可能调用同一工具：优先取本检查项自己的调用记录，
            // 兼容未落 ChecklistItemId 的历史记录时再回退到本项范围内最近一条。
            .Where(log => log.ChecklistItemId == item.ItemId || log.ChecklistItemId is null)
            .OrderByDescending(log => log.ChecklistItemId == item.ItemId)
            .ThenByDescending(log => log.CreatedAt)
            .FirstOrDefault();

        if (candidate is null)
        {
            return Error($"找不到工具 {item.ToolName} 的成功调用记录，无法复核。");
        }

        if (candidate.Truncated)
        {
            return Error("工具结果因超长被截断，无法复核。");
        }

        if (string.IsNullOrWhiteSpace(candidate.Result))
        {
            return Error("工具结果为空，无法复核。");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(candidate.Result);
        }
        catch (JsonException ex)
        {
            return Error($"工具结果不是合法 JSON：{ex.Message}");
        }

        using (document)
        {
            if (!JsonPathReader.TryResolve(document.RootElement, item.ValuePath, out var value))
            {
                return Error($"按取值路径「{item.ValuePath}」找不到值，无法复核。");
            }

            var valueText = JsonValueText.Format(value);
            if (!TryCompare(item.Operator.Value, valueText, item.Threshold, out var satisfied, out var compareError))
            {
                return Error(compareError ?? "断言无法执行。");
            }

            return new FrameworkVerdictResult(
                satisfied ? FrameworkVerdictStatus.Ok : FrameworkVerdictStatus.Abnormal,
                valueText,
                null);
        }
    }

    /// <summary>
    /// 比较符语义：Lt/Lte/Gt/Gte 只接受数值（invariant decimal）；
    /// Eq/Ne 数字优先，否则用 Ordinal 字符串比较；Contains/NotContains 用 Ordinal 子串判断。
    /// </summary>
    public static bool TryCompare(
        ComparisonOperator comparison,
        string value,
        string threshold,
        out bool satisfied,
        out string? error)
    {
        satisfied = false;
        error = null;

        decimal left = 0;
        decimal right = 0;
        var numeric = TryParseDecimal(value, out left) && TryParseDecimal(threshold, out right);

        switch (comparison)
        {
            case ComparisonOperator.Lt:
            case ComparisonOperator.Lte:
            case ComparisonOperator.Gt:
            case ComparisonOperator.Gte:
                if (!numeric)
                {
                    error = $"比较符 {comparison} 需要数值，实际值为「{value}」、阈值为「{threshold}」。";
                    return false;
                }

                satisfied = comparison switch
                {
                    ComparisonOperator.Lt => left < right,
                    ComparisonOperator.Lte => left <= right,
                    ComparisonOperator.Gt => left > right,
                    _ => left >= right,
                };
                return true;

            case ComparisonOperator.Eq:
                satisfied = numeric
                    ? left == right
                    : string.Equals(value, threshold, StringComparison.Ordinal);
                return true;

            case ComparisonOperator.Ne:
                satisfied = numeric
                    ? left != right
                    : !string.Equals(value, threshold, StringComparison.Ordinal);
                return true;

            case ComparisonOperator.Contains:
                satisfied = value.Contains(threshold, StringComparison.Ordinal);
                return true;

            case ComparisonOperator.NotContains:
                satisfied = !value.Contains(threshold, StringComparison.Ordinal);
                return true;

            default:
                error = $"不支持的比较符：{comparison}。";
                return false;
        }
    }

    private static bool TryParseDecimal(string text, out decimal value)
        => decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static FrameworkVerdictResult Error(string message)
        => new(FrameworkVerdictStatus.Error, null, message);
}

/// <summary>从工具结果 JSON 中按简单路径取值，支持 <c>$.a.b[0]</c> 与 <c>a.b</c>。</summary>
public static class JsonPathReader
{
    public static bool TryResolve(JsonElement root, string? path, out JsonElement value)
    {
        value = root;
        if (string.IsNullOrWhiteSpace(path) || path.Trim() == "$")
        {
            return true;
        }

        var remaining = path.Trim();
        if (remaining.StartsWith("$.", StringComparison.Ordinal))
        {
            remaining = remaining[2..];
        }
        else if (remaining.StartsWith('$'))
        {
            remaining = remaining[1..];
        }

        if (remaining.StartsWith('.'))
        {
            remaining = remaining[1..];
        }

        var index = 0;
        while (index < remaining.Length)
        {
            var current = remaining[index];
            if (current == '.')
            {
                index++;
                continue;
            }

            if (current == '[')
            {
                var close = remaining.IndexOf(']', index);
                if (close < 0)
                {
                    return false;
                }

                var indexText = remaining[(index + 1)..close].Trim().Trim('\'', '"');
                if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var arrayIndex)
                    || arrayIndex < 0
                    || value.ValueKind != JsonValueKind.Array
                    || arrayIndex >= value.GetArrayLength())
                {
                    return false;
                }

                value = value[arrayIndex];
                index = close + 1;
                continue;
            }

            var end = index;
            while (end < remaining.Length && remaining[end] != '.' && remaining[end] != '[')
            {
                end++;
            }

            var propertyName = remaining[index..end];
            if (propertyName.Length == 0
                || value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty(propertyName, out var child))
            {
                return false;
            }

            value = child;
            index = end;
        }

        return true;
    }
}

/// <summary>把 JSON 值转成稳定文本：字符串取原文，其余取紧凑 JSON 文本。</summary>
public static class JsonValueText
{
    public static string Format(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Null => "null",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText(),
    };
}
