using System.Text;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using Microsoft.Extensions.AI;

namespace GrowthInspector.Agents.Execution;

/// <summary>
/// 执行 agent 的提示词：一次性给出通用手册全文、被引用专用手册、全部检查项断言、
/// 当前检查项上下文与 finding 历史，要求模型先取证再输出单个结构化 claim。
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
            2. 判定型检查项（带 Operator / Threshold / ValuePath）必须调用声明中指定的工具，并从返回 JSON 中读取对应路径的值。
            3. 结构化断言描述的是「健康状态」：断言满足 = Ok，断言不满足 = Abnormal；无法取证 = InsufficientEvidence。
            4. 不要输出思考过程、Markdown 代码块或多个 JSON。最终回复只输出一个 JSON 对象。
            5. 工具名前缀：本地手册工具为 local__；MCP 工具为 {server}__{tool}。toolName 必须填写实际调用过的完整工具名。

            最终 JSON 结构：
            {
              "checklistItemId": "当前检查项 Id",
              "toolName": "实际调用的完整工具名，没有调用则为空字符串",
              "value": "从工具结果中读取到的值（字符串）",
              "evidence": "支撑判定的证据摘要，引用工具返回的关键字段",
              "verdict": "Ok | Abnormal | InsufficientEvidence",
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

        builder.AppendLine("## 本检查单全部检查项及其断言");
        if (snapshot is null || snapshot.Items.Count == 0)
        {
            builder.AppendLine($"- {request.Item.Title}（仅当前项可用）");
        }
        else
        {
            foreach (var item in snapshot.Items.OrderBy(item => item.SortOrder))
            {
                builder.Append("- [").Append(item.SortOrder).Append("] ").Append(item.Title);
                if (item.Operator is not null)
                {
                    builder.Append("；断言：")
                        .Append(item.ToolName)
                        .Append(' ')
                        .Append(item.ValuePath)
                        .Append(' ')
                        .Append(item.Operator)
                        .Append(' ')
                        .Append(item.Threshold);
                    if (!string.IsNullOrWhiteSpace(item.Window))
                    {
                        builder.Append("（窗口 ").Append(item.Window).Append('）');
                    }
                }
                else
                {
                    builder.Append("；无结构化断言（信息型）");
                }

                if (!string.IsNullOrWhiteSpace(item.DataSourceHint))
                {
                    builder.Append("；数据源提示：").Append(item.DataSourceHint);
                }

                builder.AppendLine();
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 当前检查项");
        builder.AppendLine($"- Id：{request.Item.ItemId}");
        builder.AppendLine($"- 标题：{request.Item.Title}");
        builder.AppendLine($"- 规则版本：{request.Item.RuleVersion}");
        builder.AppendLine($"- 严重级别：{request.Item.Severity?.ToString() ?? "未设置"}");

        if (request.Item.Operator is not null)
        {
            builder.AppendLine("- 结构化断言：");
            builder.AppendLine($"  - 工具名：{request.Item.ToolName}");
            builder.AppendLine($"  - 参数模板：{request.Item.ArgumentsTemplate ?? "（无）"}");
            builder.AppendLine($"  - 取值路径：{request.Item.ValuePath ?? "（根节点）"}");
            builder.AppendLine($"  - 比较符：{request.Item.Operator}");
            builder.AppendLine($"  - 阈值：{request.Item.Threshold}");
            builder.AppendLine($"  - 窗口：{request.Item.Window ?? "（未设置）"}");
        }
        else
        {
            builder.AppendLine("- 本项为信息型检查项：没有结构化断言，只收集证据并给出 InsufficientEvidence 或观察结论。");
        }

        if (!string.IsNullOrWhiteSpace(request.Item.Expectation))
        {
            builder.AppendLine($"- 预期说明：{request.Item.Expectation}");
        }

        if (!string.IsNullOrWhiteSpace(request.Item.DataSourceHint))
        {
            builder.AppendLine($"- 数据源提示：{request.Item.DataSourceHint}");
        }

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
                    .Append("；规则版本 ").Append(finding.RuleVersion)
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
        builder.AppendLine("先调用必要工具取证，然后只输出最终 JSON 对象。没有断言信息的检查项不要臆造数值。");
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
