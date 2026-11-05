using GrowthInspector.Core.Models;
using GrowthInspector.Web.Services;

namespace GrowthInspector.Tests;

/// <summary>
/// 总览「需要处理」看板：分层顺序、封顶与真实计数、已恢复折叠段的排序都不能出错。
/// 纯函数测试，不依赖数据库或 Blazor 渲染。
/// </summary>
public sealed class FindingsBoardBuilderTests
{
    private static readonly IReadOnlyDictionary<Guid, (string? Name, bool Archived)> EmptyNames =
        new Dictionary<Guid, (string?, bool)>();

    [Fact]
    public void Tiers_are_ordered_critical_warning_then_info_tail()
    {
        var now = DateTimeOffset.UtcNow;
        var board = FindingsBoardBuilder.Build(
            [
                Finding(Severity.Info, now.AddMinutes(-1)),
                Finding(Severity.Warning, now.AddMinutes(-2)),
                Finding(Severity.Critical, now.AddMinutes(-3)),
                Finding(Severity.Critical, now.AddMinutes(-4)),
            ],
            new Dictionary<Severity, int> { [Severity.Critical] = 2, [Severity.Warning] = 1, [Severity.Info] = 2 },
            [],
            0,
            EmptyNames,
            EmptyNames);

        Assert.Equal([Severity.Critical, Severity.Warning, Severity.Info], board.OpenTiers.Select(x => x.Severity));
        Assert.Equal("其他", board.OpenTiers[2].Title);
        Assert.Equal(5, board.OpenCount);
        Assert.Equal([now.AddMinutes(-3), now.AddMinutes(-4)], board.OpenTiers[0].Rows.Select(x => x.Finding.LastSeenAt));
    }

    [Fact]
    public void Empty_tiers_are_not_emitted()
    {
        var board = FindingsBoardBuilder.Build(
            [Finding(Severity.Warning, DateTimeOffset.UtcNow)],
            new Dictionary<Severity, int> { [Severity.Critical] = 0, [Severity.Warning] = 1, [Severity.Info] = 0 },
            [],
            0,
            EmptyNames,
            EmptyNames);

        var tier = Assert.Single(board.OpenTiers);
        Assert.Equal(Severity.Warning, tier.Severity);
        Assert.False(tier.Truncated);
    }

    [Fact]
    public void Truncation_is_measured_against_true_counts_not_rows()
    {
        var rows = Enumerable.Range(0, 3).Select(_ => Finding(Severity.Critical, DateTimeOffset.UtcNow)).ToList();
        var board = FindingsBoardBuilder.Build(
            rows,
            new Dictionary<Severity, int> { [Severity.Critical] = 8 },
            [],
            0,
            EmptyNames,
            EmptyNames);

        var tier = Assert.Single(board.OpenTiers);
        Assert.Equal(8, tier.TotalCount);
        Assert.Equal(3, tier.Rows.Count);
        Assert.True(tier.Truncated);
        Assert.Equal(8, board.OpenCount);
    }

    [Fact]
    public void Open_rows_are_capped_at_max()
    {
        var rows = Enumerable.Range(0, FindingsBoardBuilder.MaxOpenRows + 10)
            .Select(_ => Finding(Severity.Critical, DateTimeOffset.UtcNow)).ToList();
        var board = FindingsBoardBuilder.Build(
            rows,
            new Dictionary<Severity, int> { [Severity.Critical] = rows.Count },
            [],
            0,
            EmptyNames,
            EmptyNames);

        Assert.Equal(FindingsBoardBuilder.MaxOpenRows, board.OpenTiers.Single().Rows.Count);
    }

    [Fact]
    public void Resolved_rows_are_sorted_by_resolved_time_desc()
    {
        var now = DateTimeOffset.UtcNow;
        var older = Finding(Severity.Warning, now.AddDays(-2));
        older.ResolvedAt = now.AddHours(-3);
        var newer = Finding(Severity.Info, now.AddDays(-2));
        newer.ResolvedAt = now.AddHours(-1);
        // 没有 ResolvedAt 的按 LastSeenAt 兜底：给一个更老的 LastSeenAt，让它排在最后。
        var fallback = Finding(Severity.Critical, now.AddDays(-3));

        var board = FindingsBoardBuilder.Build(
            [],
            new Dictionary<Severity, int>(),
            [older, fallback, newer],
            3,
            EmptyNames,
            EmptyNames);

        Assert.Equal(3, board.ResolvedCount);
        // 恢复时间倒序；没有 ResolvedAt 的按 LastSeenAt 兜底排最后。
        Assert.Equal([newer.Id, older.Id, fallback.Id], board.RecentResolved.Select(x => x.Finding.Id));
    }

    [Fact]
    public void Names_and_archived_flags_flow_into_rows()
    {
        var checklistId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var finding = Finding(Severity.Critical, DateTimeOffset.UtcNow);
        finding.ChecklistId = checklistId;
        finding.ChecklistItemId = itemId;

        var board = FindingsBoardBuilder.Build(
            [finding],
            new Dictionary<Severity, int> { [Severity.Critical] = 1 },
            [],
            0,
            new Dictionary<Guid, (string?, bool)> { [checklistId] = ("库存手册", true) },
            new Dictionary<Guid, (string?, bool)> { [itemId] = ("库存连通性", false) });

        var row = board.OpenTiers.Single().Rows.Single();
        Assert.Equal("库存手册", row.ChecklistName);
        Assert.True(row.ChecklistArchived);
        Assert.Equal("库存连通性", row.ItemName);
        Assert.False(row.ItemArchived);
        Assert.Equal($"/findings/{finding.Id}", row.DetailHref);
    }

    [Fact]
    public void Missing_name_maps_render_as_null_not_throw()
    {
        var finding = Finding(Severity.Warning, DateTimeOffset.UtcNow);
        var board = FindingsBoardBuilder.Build(
            [finding],
            new Dictionary<Severity, int> { [Severity.Warning] = 1 },
            [],
            0,
            EmptyNames,
            EmptyNames);

        var row = board.OpenTiers.Single().Rows.Single();
        Assert.Null(row.ChecklistName);
        Assert.Null(row.ItemName);
    }

    private static Finding Finding(Severity severity, DateTimeOffset lastSeenAt) => new()
    {
        Severity = severity,
        Status = FindingStatus.Open,
        LastSeenAt = lastSeenAt,
        FirstSeenAt = lastSeenAt.AddDays(-1),
    };
}
