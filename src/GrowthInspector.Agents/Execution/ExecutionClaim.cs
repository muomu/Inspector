using System.Text.Json;
using GrowthInspector.Core.Models;

namespace GrowthInspector.Agents.Execution;

/// <summary>执行 agent 输出的结构化 claim。</summary>
public sealed record ExecutionClaim(
    Guid ChecklistItemId,
    string? ToolName,
    string? Value,
    string? Evidence,
    VerdictStatus Verdict,
    string? Narrative,
    IReadOnlyList<string> Observations);

/// <summary>
/// claim 解析：容忍模型在 JSON 前后加说明或 Markdown 代码块，但只接受一个完整 JSON 对象；
/// checklistItemId 必须与当前检查项一致，verdict 必须是三态之一，否则视为未完成 LLM 判定。
/// </summary>
public static class ExecutionClaimParser
{
    public static bool TryParse(
        string? text,
        Guid expectedItemId,
        out ExecutionClaim? claim,
        out string? error)
    {
        claim = null;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "模型未输出任何文本。";
            return false;
        }

        var json = ExtractJsonObject(text);
        if (json is null)
        {
            error = "模型输出中找不到 JSON 对象。";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            error = $"claim 不是合法 JSON：{ex.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "claim 必须是 JSON 对象。";
                return false;
            }

            if (!TryGetString(root, "checklistItemId", out var itemIdText) || !Guid.TryParse(itemIdText, out var itemId))
            {
                error = "claim 缺少合法的 checklistItemId。";
                return false;
            }

            if (itemId != expectedItemId)
            {
                error = $"claim 的 checklistItemId（{itemId}）与当前检查项（{expectedItemId}）不一致。";
                return false;
            }

            if (!TryGetString(root, "verdict", out var verdictText)
                || !TryParseVerdict(verdictText, out var verdict))
            {
                error = "claim 的 verdict 必须是 Ok / Abnormal / InsufficientEvidence。";
                return false;
            }

            var observations = new List<string>();
            if (root.TryGetProperty("observations", out var observationsElement)
                && observationsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in observationsElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } observation)
                    {
                        observations.Add(observation);
                    }
                }
            }

            claim = new ExecutionClaim(
                itemId,
                TryGetString(root, "toolName", out var toolName) ? toolName : null,
                TryGetString(root, "value", out var value) ? value : null,
                TryGetString(root, "evidence", out var evidence) ? evidence : null,
                verdict,
                TryGetString(root, "narrative", out var narrative) ? narrative : null,
                observations);

            return true;
        }
    }

    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out var element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? string.Empty;
            return true;
        }

        if (element.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
        {
            value = element.GetRawText();
            return true;
        }

        return false;
    }

    private static bool TryParseVerdict(string text, out VerdictStatus verdict)
    {
        verdict = default;
        var normalized = text.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return normalized switch
        {
            "ok" => Set(VerdictStatus.Ok, out verdict),
            "abnormal" => Set(VerdictStatus.Abnormal, out verdict),
            "insufficientevidence" => Set(VerdictStatus.InsufficientEvidence, out verdict),
            _ => false,
        };

        static bool Set(VerdictStatus value, out VerdictStatus target)
        {
            target = value;
            return true;
        }
    }
}
