using System.Text;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using Microsoft.Extensions.AI;

namespace GrowthInspector.Agents.Execution;

/// <summary>
/// 执行 agent 的提示词：一次性给出项目运维上下文、通用手册全文、被引用专用手册、
/// 本检查单全部检查项标题、当前检查项的自然语言正文与 finding 历史，要求模型先取证再输出单个结构化 claim。
/// </summary>
public static class ExecutionPromptBuilder
{
    public static List<ChatMessage> Build(
        ChecklistItemExecutionRequest request,
        ChecklistSnapshot? snapshot)
    {
        var system = """
            你是巡检框架的「检查项执行 agent」。你的工作只有两件事：调用工具取证，然后输出结构化判定。

            规则：
            1. 只能使用提供的工具取证，不得编造工具、指标或日志内容；没有证据时如实说明。
            2. 检查项是一段自然语言描述：查什么、什么算健康、异常先看哪都写在正文里。按正文判断，不要臆造阈值。
            3. 正文提到的系统、指标或日志线索，可在项目上下文与注入的手册里继续查证。
            4. 判定三态：Ok（符合正文健康口径）/ Abnormal（不符合）/ InsufficientEvidence（取证不到、无法判断）。
            5. 不要输出思考过程、Markdown 代码块或多个 JSON。最终回复只输出一个 JSON 对象。
            6. 工具名形如 {server}__{tool}，按需调用；调用失败可在 evidence 里说明。

            最终 JSON 结构：
            {
              "checklistItemId": "当前检查项 Id",
              "verdict": "Ok | Abnormal | InsufficientEvidence",
              "evidence": "支撑判定的证据摘要，引用工具返回的关键字段",
              "narrative": "给运维人员看的简短结论",
              "observations": ["可选的补充观察"]
            }
            """;

        var user = BuildContext(request, snapshot);
        return
        [
            new ChatMessage(ChatRole.System, system),
            new ChatMessage(ChatRole.User, user),
        ];
    }

    private static string BuildContext(ChecklistItemExecutionRequest request, ChecklistSnapshot? snapshot)
    {
        var builder = new StringBuilder();

        builder.AppendLine("## 项目运维上下文");
        builder.AppendLine(string.IsNullOrWhiteSpace(request.ProjectOpsContext)
            ? "（未配置）"
            : request.ProjectOpsContext.Trim());
        builder.AppendLine();

        builder.AppendLine("## 通用运维手册（项目级，自动注入）");
        AppendRunbooks(builder, request.GeneralRunbooks);
        builder.AppendLine();

        builder.AppendLine("## 被检查单引用的专用手册");
        AppendRunbooks(builder, request.ReferencedRunbooks);
        builder.AppendLine();

        builder.AppendLine("## 本检查单全部检查项（标题与级别，供了解检查面）");
        if (snapshot is null || snapshot.Items.Count == 0)
        {
            builder.AppendLine($"- {request.Item.Title}");
        }
        else
        {
            foreach (var item in snapshot.Items.OrderBy(item => item.SortOrder))
            {
                builder.Append("- ").Append(item.Title);
                if (item.Severity is not null)
                {
                    builder.Append("（").Append(item.Severity).Append('）');
                }

                builder.AppendLine();
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 当前检查项");
        builder.AppendLine($"- Id：{request.Item.ItemId}");
        builder.AppendLine($"- 标题：{request.Item.Title}");
        builder.AppendLine($"- 严重级别：{request.Item.Severity?.ToString() ?? "未设置"}");
        builder.AppendLine("- 正文：");
        builder.AppendLine(request.Item.Body.Trim());

        builder.AppendLine();
        builder.AppendLine("## 当前检查项的 finding 历史");
        if (request.FindingHistory.Count == 0)
        {
            builder.AppendLine("（无历史 finding）");
        }
        else
        {
            foreach (var finding in request.FindingHistory.OrderByDescending(item => item.LastSeenAt))
            {
                builder.Append("- ").Append(finding.Status)
                    .Append("；首次 ").Append(finding.FirstSeenAt.ToString("yyyy-MM-dd HH:mm:ss"))
                    .Append("；最近 ").Append(finding.LastSeenAt.ToString("yyyy-MM-dd HH:mm:ss"));
                if (finding.ResolvedAt is { } resolvedAt)
                {
                    builder.Append("；恢复于 ").Append(resolvedAt.ToString("yyyy-MM-dd HH:mm:ss"));
                }

                builder.AppendLine();
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 输出要求");
        builder.AppendLine("先调用必要工具取证，然后只输出最终 JSON 对象。取证不到就如实给 InsufficientEvidence，不要臆造数值。");
        return builder.ToString();
    }

    private static void AppendRunbooks(StringBuilder builder, IReadOnlyList<RunbookSnapshot> runbooks)
    {
        if (runbooks.Count == 0)
        {
            builder.AppendLine("（无）");
            return;
        }

        foreach (var runbook in runbooks)
        {
            builder.Append("### ").AppendLine(runbook.Title);
            builder.AppendLine(runbook.Body.Trim());
            builder.AppendLine();
        }
    }
}
