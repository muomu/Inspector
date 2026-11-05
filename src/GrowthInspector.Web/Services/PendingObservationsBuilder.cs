using GrowthInspector.Core.Models;

namespace GrowthInspector.Web.Services;

/// <summary>待证观察行：最近一轮里 Error / 证据不足的检查项，不建 Finding、不发 Lark，只进日报观察。</summary>
public sealed record PendingObservationRow(
    CheckResult Result,
    string Title,
    bool Archived,
    StatusPresentation Presentation)
{
    public string DetailText => DisplayText.Shorten(Result.Narrative ?? Result.ErrorMessage, 140);
}

/// <summary>
/// 从一轮执行的检查项结果里挑出「待证」：判定型的 Error / 证据不足算待证，
/// 信息型的证据不足是中性「信息」，不算待证。纯函数，总览与执行详情共用同一口径。
/// </summary>
public static class PendingObservationsBuilder
{
    public static IReadOnlyList<PendingObservationRow> Build(
        IEnumerable<(CheckResult Result, string Title, bool Archived, Severity? Severity, StatusItemKind Kind, int SortOrder)> rows)
        => rows
            .Select(row => (Row: row, Presentation: StatusPresenter.FromResult(row.Result.Status, row.Severity, row.Kind)))
            .Where(x => x.Presentation.State == StatusState.Pending)
            .OrderByDescending(x => x.Row.Result.Status)
            .ThenBy(x => x.Row.SortOrder)
            .Select(x => new PendingObservationRow(x.Row.Result, x.Row.Title, x.Row.Archived, x.Presentation))
            .ToList();
}
