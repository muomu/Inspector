using GrowthInspector.Core.Models;

namespace GrowthInspector.Notifications.Lark;

/// <summary>
/// 通知报文模板。v1 不引入 LLM 生成文案，全部走固定模板；
/// 名称 / 证据缺失时使用占位文案兜底，保证 mock 与真实通道都能发出可读消息。
/// </summary>
public static class LarkMessageTemplates
{
    public static string FindingOpened(
        string? projectName,
        string? environmentName,
        string? checklistName,
        string? itemTitle,
        Severity severity,
        string? evidenceSummary,
        string? narrative,
        string occurredAt) =>
        $"""
         **新异常**
         - 项目：{Fallback(projectName, "（未知项目）")}
         - 环境：{Fallback(environmentName, "（未知环境）")}
         - 检查单：{Fallback(checklistName, "（检查单已删除）")}
         - 检查项：{Fallback(itemTitle, "（检查项已删除）")}
         - 级别：{severity}
         - 首次发现：{occurredAt}
         - 说明：{Fallback(narrative, "（无）")}
         - 证据摘要：{Fallback(evidenceSummary, "（无）")}
         """;

    public static string FindingResolved(
        string? projectName,
        string? environmentName,
        string? checklistName,
        string? itemTitle,
        Severity severity,
        string firstSeenAt,
        string lastSeenAt,
        string resolvedAt) =>
        $"""
         **异常恢复**
         - 项目：{Fallback(projectName, "（未知项目）")}
         - 环境：{Fallback(environmentName, "（未知环境）")}
         - 检查单：{Fallback(checklistName, "（检查单已删除）")}
         - 检查项：{Fallback(itemTitle, "（检查项已删除）")}
         - 级别：{severity}
         - 首次发现：{firstSeenAt}
         - 最近出现：{lastSeenAt}
         - 恢复时间：{resolvedAt}
         """;

    private static string Fallback(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
