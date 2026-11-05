using GrowthInspector.Core.Models;

namespace GrowthInspector.Web.Services;

/// <summary>异常时间线上的一格：哪一轮、判成什么、agent 说了什么；跳转回该轮执行详情。</summary>
public sealed record FindingTimelineEntry(
    Guid RunId,
    DateTimeOffset RunStartedAt,
    CheckResultStatus Status,
    VerdictStatus? AgentVerdict,
    string? Detail,
    StatusPresentation Presentation);

/// <summary>
/// Finding 实体只存首次 / 最近 / 恢复三个时间点，不存历史；
/// 时间线由该检查项在历史执行里的结果重建：同一轮只取一条，只看最近 N 轮。
/// 执行审计默认留存 90 天，更早的历史会随清理消失——这是「重建而非落表」的已知代价。
/// </summary>
public static class FindingTimelineBuilder
{
    public const int RecentRunLimit = 30;

    /// <param name="fallbackSeverity">检查项已删除 / 未配置级别时，用 finding 自己的级别兜底。</param>
    /// <param name="fallbackSeverity">检查项已删除 / 未配置级别时，用 finding 自己的级别兜底。</param>
    /// <param name="itemResults">该检查项的历史结果，须带 <see cref="CheckResult.ChecklistRun"/>；顺序不限。</param>
    public static IReadOnlyList<FindingTimelineEntry> Build(
        Severity fallbackSeverity,
        IEnumerable<CheckResult> itemResults)
    {
        var byRun = itemResults
            .Where(x => x.ChecklistRun is not null)
            .GroupBy(x => x.ChecklistRunId)
            .Select(group => group
                .OrderByDescending(x => x.CreatedAt)
                .First())
            .OrderByDescending(x => x.ChecklistRun!.StartedAt)
            .Take(RecentRunLimit);

        return byRun
            .Select(x => new FindingTimelineEntry(
                x.ChecklistRunId,
                x.ChecklistRun!.StartedAt,
                x.Status,
                x.AgentVerdict,
                string.IsNullOrWhiteSpace(x.Narrative) ? x.ErrorMessage : x.Narrative,
                StatusPresenter.FromResult(x.Status, fallbackSeverity)))
            .OrderBy(x => x.RunStartedAt)
            .ToList();
    }
}
