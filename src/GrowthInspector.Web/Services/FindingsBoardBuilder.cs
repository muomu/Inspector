using GrowthInspector.Core.Models;

namespace GrowthInspector.Web.Services;

/// <summary>异常卡片行：列表只渲染这里给到的名字与归档态，详情跳转由 <see cref="DetailHref"/> 给出。</summary>
public sealed record FindingCardRow(
    Finding Finding,
    string? ChecklistName,
    bool ChecklistArchived,
    string? ItemName,
    bool ItemArchived,
    StatusPresentation Severity)
{
    public string DetailHref => $"/findings/{Finding.Id}";
}

/// <summary>严重级分层。Rows 是封顶后的展示行，TotalCount 是该级真实数量，两者差即「还有更多」。</summary>
public sealed record FindingsTier(
    Severity Severity,
    string Title,
    IReadOnlyList<FindingCardRow> Rows,
    int TotalCount)
{
    public bool Truncated => Rows.Count < TotalCount;
}

/// <summary>
/// 总览「需要处理」的异常看板：未恢复按严重级分层（Critical → Warning → 其他），
/// 已恢复不占分诊动线，只留最近一段折叠列表。纯函数，便于单测锁定分层与封顶边界。
/// </summary>
public sealed record FindingsBoard(
    IReadOnlyList<FindingsTier> OpenTiers,
    int OpenCount,
    IReadOnlyList<FindingCardRow> RecentResolved,
    int ResolvedCount)
{
    public static FindingsBoard Empty { get; } = new([], 0, [], 0);
}

public static class FindingsBoardBuilder
{
    /// <summary>未恢复异常的整体展示上限；超出部分以「还有更多」提示，不做分页。</summary>
    public const int MaxOpenRows = 200;

    /// <summary>最近已恢复折叠段的展示上限。</summary>
    public const int MaxResolvedRows = 20;

    private static readonly Severity[] TierOrder = [Severity.Critical, Severity.Warning, Severity.Info];

    private static readonly IReadOnlyDictionary<Severity, string> TierTitles =
        new Dictionary<Severity, string>
        {
            [Severity.Critical] = "Critical",
            [Severity.Warning] = "Warning",
            [Severity.Info] = "其他",
        };

    public static FindingsBoard Build(
        IReadOnlyList<Finding> openFindings,
        IReadOnlyDictionary<Severity, int> openCounts,
        IReadOnlyList<Finding> recentResolved,
        int resolvedCount,
        IReadOnlyDictionary<Guid, (string? Name, bool Archived)> checklists,
        IReadOnlyDictionary<Guid, (string? Name, bool Archived)> items)
    {
        // 枚举在 SQLite 里落成 TEXT，调用方若直接按列排序会变字典序；这里用数值权重显式降序。
        // 调用方通常已在查询里 Take(MaxOpenRows)，这里再兜底一次，保证「整体封顶」语义不被绕过。
        var ordered = openFindings
            .OrderByDescending(x => x.Severity)
            .ThenByDescending(x => x.LastSeenAt)
            .Take(MaxOpenRows)
            .ToList();

        var tiers = TierOrder
            .Where(severity => openCounts.GetValueOrDefault(severity) > 0)
            .Select(severity => new FindingsTier(
                severity,
                TierTitles[severity],
                ordered.Where(x => x.Severity == severity).Select(x => ToRow(x, checklists, items)).ToList(),
                openCounts.GetValueOrDefault(severity)))
            .ToList();

        var resolved = recentResolved
            .OrderByDescending(x => x.ResolvedAt ?? x.LastSeenAt)
            .Select(x => ToRow(x, checklists, items))
            .ToList();

        return new FindingsBoard(tiers, openCounts.Values.Sum(), resolved, resolvedCount);
    }

    private static FindingCardRow ToRow(
        Finding finding,
        IReadOnlyDictionary<Guid, (string? Name, bool Archived)> checklists,
        IReadOnlyDictionary<Guid, (string? Name, bool Archived)> items)
    {
        var checklist = checklists.GetValueOrDefault(finding.ChecklistId);
        var item = items.GetValueOrDefault(finding.ChecklistItemId);
        return new FindingCardRow(
            finding,
            checklist.Name,
            checklist.Archived,
            item.Name,
            item.Archived,
            StatusPresenter.FromSeverity(finding.Severity));
    }
}
