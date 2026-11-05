using GrowthInspector.Core.Models;
using GrowthInspector.Web.Services;

namespace GrowthInspector.Tests;

/// <summary>
/// 待证观察口径：判定型的 Error / 证据不足算待证，信息型的证据不足是中性「信息」，
/// Ok / Abnormal 不进观察段。总览与执行详情共用这一份判定，不能各写一套。
/// </summary>
public sealed class PendingObservationsBuilderTests
{
    [Fact]
    public void Error_comes_before_insufficient_evidence_then_by_sort_order()
    {
        var rows = new List<(CheckResult, string, bool, Severity?, StatusItemKind, int)>
        {
            (Result(CheckResultStatus.InsufficientEvidence), "项目 B", false, Severity.Warning, StatusItemKind.Judgement, 3),
            (Result(CheckResultStatus.Error), "项目 D", false, Severity.Warning, StatusItemKind.Judgement, 4),
            (Result(CheckResultStatus.InsufficientEvidence), "项目 A", false, Severity.Warning, StatusItemKind.Judgement, 1),
            (Result(CheckResultStatus.Error), "项目 C", false, Severity.Warning, StatusItemKind.Judgement, 2),
        };

        var pending = PendingObservationsBuilder.Build(rows);

        Assert.Equal(4, pending.Count);
        Assert.Equal(["项目 C", "项目 D", "项目 A", "项目 B"], pending.Select(x => x.Title));
    }

    [Fact]
    public void Informational_insufficient_evidence_is_not_pending()
    {
        var rows = new List<(CheckResult, string, bool, Severity?, StatusItemKind, int)>
        {
            (Result(CheckResultStatus.InsufficientEvidence), "信息收集项", false, Severity.Info, StatusItemKind.Informational, 1),
        };

        Assert.Empty(PendingObservationsBuilder.Build(rows));
    }

    [Fact]
    public void Ok_and_abnormal_are_not_pending()
    {
        var rows = new List<(CheckResult, string, bool, Severity?, StatusItemKind, int)>
        {
            (Result(CheckResultStatus.Ok), "正常项", false, Severity.Warning, StatusItemKind.Judgement, 1),
            (Result(CheckResultStatus.Abnormal), "异常项", false, Severity.Critical, StatusItemKind.Judgement, 2),
        };

        Assert.Empty(PendingObservationsBuilder.Build(rows));
    }

    [Fact]
    public void Detail_prefers_narrative_and_shortens()
    {
        var narrative = Result(CheckResultStatus.Error);
        narrative.Narrative = new string('证', 200);
        var errorOnly = Result(CheckResultStatus.Error);
        errorOnly.ErrorMessage = "MCP 调用失败";

        var pending = PendingObservationsBuilder.Build(
        [
            (narrative, "长说明", false, Severity.Warning, StatusItemKind.Judgement, 1),
            (errorOnly, "错误信息", true, Severity.Warning, StatusItemKind.Judgement, 2),
        ]);

        Assert.Equal(2, pending.Count);
        Assert.EndsWith("…", pending[0].DetailText);
        Assert.True(pending[0].DetailText.Length <= 141);
        Assert.Equal("MCP 调用失败", pending[1].DetailText);
        Assert.True(pending[1].Archived);
    }

    private static CheckResult Result(CheckResultStatus status) => new()
    {
        Status = status,
    };
}
