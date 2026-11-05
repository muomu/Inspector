using System.Text;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using Microsoft.Extensions.AI;

namespace GrowthInspector.Agents.Execution;

/// <summary>
/// 执行 agent 的提示词，分两段：
/// 会话开场（system + 项目上下文 + 手册全文 + 检查单概览，整轮只付一次，是前缀缓存的稳定前缀）；
/// 逐项推进（每个检查项一条 user 消息：第 k/N 项 + 正文 + finding 历史）。
/// </summary>
public static class ExecutionPromptBuilder
{
    private const string SystemPrompt = """
        你是巡检框架的「检查单执行 agent」。你将在同一个会话里依次收到本检查单的每个检查项，逐项取证、逐项判定。

        规则：
        1. 只能使用提供的工具取证，不得编造工具、指标或日志内容；没有证据时如实说明。
        2. 检查项是一段自然语言描述：查什么、什么算健康、异常先看哪都写在正文里。按正文判断，不要臆造阈值。
        3. 正文提到的系统、指标或日志线索，可在项目上下文与注入的手册里继续查证。
        4. 判定三态：Ok（符合正文健康口径）/ Abnormal（不符合）/ InsufficientEvidence（取证不到、无法判断）。
        5. 取证纪律：先想清楚要查什么再调用工具；会话里已有的证据直接复用，不要重复取证；同一工具同样的参数绝不重复调用；证据足够时立即输出结论，不要继续探索。
        6. 每个检查项的最终回复只输出该项的一个 JSON 对象，不要输出思考过程、Markdown 代码块或多个 JSON。
        7. 工具名形如 {server}__{tool}，按需调用；调用失败可在 evidence 里说明。

        最终 JSON 结构：
        {
          "checklistItemId": "当前检查项 Id",
          "verdict": "Ok | Abnormal | InsufficientEvidence",
          "evidence": "支撑判定的证据摘要，引用工具返回的关键字段",
          "narrative": "给运维人员看的简短结论",
          "observations": ["可选的补充观察"]
        }
        """;

    /// <summary>会话开场：system + 共享上下文。整轮一次，作为稳定前缀供 provider 缓存命中。</summary>
    public static List<ChatMessage> BuildSessionIntro(ChecklistRunAgentRequest request)
    {
        var user = BuildIntroContext(request);
        return
        [
            new ChatMessage(ChatRole.System, SystemPrompt),
            new ChatMessage(ChatRole.User, user),
        ];
    }

    /// <summary>第 index/total 个检查项的推进消息：正文 + finding 历史 + 输出要求。</summary>
    public static string BuildItemPrompt(
        int index,
        int total,
        ChecklistSnapshotItem item,
        IReadOnlyList<FindingSnapshot> findingHistory)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"## 第 {index}/{total} 个检查项：{item.Title}");
        builder.AppendLine($"- Id：{item.ItemId}");
        builder.AppendLine($"- 严重级别：{item.Severity?.ToString() ?? "未设置"}");
        builder.AppendLine("- 正文：");
        builder.AppendLine(string.IsNullOrWhiteSpace(item.Body) ? "（无正文）" : item.Body.Trim());
        builder.AppendLine();

        builder.AppendLine("### 本检查项的 finding 历史");
        if (findingHistory.Count == 0)
        {
            builder.AppendLine("（无历史 finding）");
        }
        else
        {
            foreach (var finding in findingHistory.OrderByDescending(finding => finding.LastSeenAt))
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
        builder.AppendLine("现在请取证并输出本项的 JSON claim（checklistItemId 必须是上方的 Id）。取证不到就如实给 InsufficientEvidence，不要臆造数值。");
        return builder.ToString();
    }

    /// <summary>单项失败后注入的纠偏说明，让会话能干净地推进到下一项。</summary>
    public static string BuildItemFailureNote(string reason)
        => $"（系统提示：上一检查项的判定未完成，原因：{reason}。已记录为 Error。继续下一个检查项时务必只输出一个 JSON 对象。）";

    private static string BuildIntroContext(ChecklistRunAgentRequest request)
    {
        var builder = new StringBuilder();

        builder.AppendLine("## 项目运维上下文");
        builder.AppendLine(string.IsNullOrWhiteSpace(request.ProjectOpsContext)
            ? "（未配置）"
            : request.ProjectOpsContext.Trim());
        builder.AppendLine();

        builder.AppendLine("## 通用运维手册（项目级，自动注入）");
        AppendRunbooks(builder, request.Snapshot.GeneralRunbooks);
        builder.AppendLine();

        builder.AppendLine("## 被检查单引用的专用手册");
        AppendRunbooks(builder, request.Snapshot.ReferencedRunbooks);
        builder.AppendLine();

        builder.AppendLine("## 本检查单全部检查项（标题与级别，供了解检查面）");
        foreach (var item in request.Snapshot.Items.OrderBy(item => item.SortOrder))
        {
            builder.Append("- ").Append(item.Title);
            if (item.Severity is not null)
            {
                builder.Append("（").Append(item.Severity).Append('）');
            }

            builder.AppendLine();
        }

        builder.AppendLine();
        builder.AppendLine("接下来将逐个给出检查项正文，请按开场规则逐项取证并输出判定。");
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
