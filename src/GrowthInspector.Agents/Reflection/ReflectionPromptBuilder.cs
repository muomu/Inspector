using System.Text;
using System.Text.Json;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using Microsoft.Extensions.AI;

namespace GrowthInspector.Agents.Reflection;

/// <summary>反思 agent 的提示词与最终 JSON 解析。</summary>
public static class ReflectionPromptBuilder
{
    public static List<ChatMessage> Build(
        ChecklistItemExecutionRequest request,
        ChecklistItemExecutionResult result)
    {
        var system = """
            你是巡检框架的「反思 agent」。你只做一件事：判断本轮检查项执行是否产生了值得写进运维手册的新知识，并只在有证据时改写手册。

            规则：
            1. 只能依据本轮执行结果与已有手册内容判断，不得编造故障、指标或排查步骤。
            2. 先用手册工具找到目标手册；没有合适目标或没有新知识时，不修改任何手册，在 narrative 中说明原因。
            3. 只改与本次执行直接相关的章节；不要做管理类 CRUD，不要创建 / 归档 / 删除手册。
            4. 工具名前缀为 local__；改写工具是 local__update_runbook_section。
            5. 最终回复只输出一个 JSON 对象，不要 Markdown 代码块。

            最终 JSON 结构：
            {
              "narrative": "本次反思结论（为什么改 / 为什么不改）",
              "writes": [
                { "runbookId": "被改写的 runbookId", "section": "章节标题", "summary": "改了什么" }
              ]
            }
            """;

        var builder = new StringBuilder();
        builder.AppendLine("## 项目运维上下文");
        builder.AppendLine(string.IsNullOrWhiteSpace(request.ProjectOpsContext)
            ? "（未配置）"
            : request.ProjectOpsContext.Trim());
        builder.AppendLine();

        builder.AppendLine("## 本轮检查项");
        builder.AppendLine($"- Id：{request.Item.ItemId}");
        builder.AppendLine($"- 标题：{request.Item.Title}");
        builder.AppendLine($"- 规则版本：{request.Item.RuleVersion}");
        builder.AppendLine($"- 结构化断言：{(request.Item.Operator is null
            ? "无（信息型）"
            : $"{request.Item.ToolName} {request.Item.ValuePath} {request.Item.Operator} {request.Item.Threshold}")}");
        builder.AppendLine();

        builder.AppendLine("## 执行结果");
        builder.AppendLine($"- 综合状态：{result.Status}");
        builder.AppendLine($"- agent 判定：{result.AgentVerdict?.ToString() ?? "（无）"}");
        builder.AppendLine($"- 框架复核：{result.FrameworkVerdict?.ToString() ?? "（无）"}");
        builder.AppendLine($"- 重算取值：{result.RecomputedValue ?? "（无）"}");
        builder.AppendLine($"- 工具调用次数：{result.ToolCallCount}");
        builder.AppendLine($"- 证据：{result.Evidence ?? "（无）"}");
        builder.AppendLine($"- 结论：{result.Narrative ?? "（无）"}");
        if (result.Observations is { Count: > 0 })
        {
            builder.AppendLine("- 观察：");
            foreach (var observation in result.Observations)
            {
                builder.AppendLine($"  - {observation}");
            }
        }

        if (result.ErrorMessage is not null)
        {
            builder.AppendLine($"- 错误：{result.ErrorMessage}");
        }

        builder.AppendLine();
        builder.AppendLine("## 现有手册");
        var runbooks = request.GeneralRunbooks.Concat(request.ReferencedRunbooks).ToList();
        if (runbooks.Count == 0)
        {
            builder.AppendLine("（当前上下文没有手册；可先用 local__list_runbooks 查找当前 Project 的手册）");
        }
        else
        {
            foreach (var runbook in runbooks)
            {
                builder.AppendLine($"### {runbook.Title}");
                builder.AppendLine($"runbookId：{runbook.RunbookId}");
                builder.AppendLine(runbook.Body.Trim());
                builder.AppendLine();
            }
        }

        builder.AppendLine("## 输出要求");
        builder.AppendLine("如果没有需要沉淀的新知识，writes 为空数组。不要输出 JSON 之外的内容。");
        return
        [
            new ChatMessage(ChatRole.System, system),
            new ChatMessage(ChatRole.User, builder.ToString()),
        ];
    }

    /// <summary>解析反思最终 JSON；非法输出返回错误，不产生写入。</summary>
    public static bool TryParse(
        string? text,
        out ReflectionOutcome? outcome,
        out string? error)
    {
        outcome = null;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "反思 agent 未输出任何文本。";
            return false;
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            error = "反思输出中找不到 JSON 对象。";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text[start..(end + 1)]);
        }
        catch (JsonException ex)
        {
            error = $"反思输出不是合法 JSON：{ex.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "反思输出必须是 JSON 对象。";
                return false;
            }

            var narrative = root.TryGetProperty("narrative", out var narrativeElement)
                && narrativeElement.ValueKind == JsonValueKind.String
                    ? narrativeElement.GetString()
                    : null;

            var writes = new List<ReflectionWrite>();
            if (root.TryGetProperty("writes", out var writesElement)
                && writesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in writesElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object
                        || !item.TryGetProperty("runbookId", out var runbookIdElement)
                        || runbookIdElement.ValueKind != JsonValueKind.String
                        || !Guid.TryParse(runbookIdElement.GetString(), out var runbookId))
                    {
                        continue;
                    }

                    var section = item.TryGetProperty("section", out var sectionElement)
                        && sectionElement.ValueKind == JsonValueKind.String
                            ? sectionElement.GetString()
                            : null;
                    var summary = item.TryGetProperty("summary", out var summaryElement)
                        && summaryElement.ValueKind == JsonValueKind.String
                            ? summaryElement.GetString()
                            : null;

                    writes.Add(new ReflectionWrite(runbookId, section, summary));
                }
            }

            outcome = new ReflectionOutcome(true, narrative, writes, null);
            return true;
        }
    }
}
