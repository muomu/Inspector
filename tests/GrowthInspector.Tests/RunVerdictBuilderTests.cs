using GrowthInspector.Core.Models;
using GrowthInspector.Web.Services;

namespace GrowthInspector.Tests;

/// <summary>执行判定分布：页面只在 RunVerdict 之上写文案，不再各自 switch 状态。</summary>
public sealed class RunVerdictBuilderTests
{
    [Fact]
    public void Empty_results_produce_empty_verdict()
    {
        var verdict = RunVerdictBuilder.Build([]);

        Assert.Equal(0, verdict.Total);
        Assert.Equal(RunVerdict.Empty, verdict);
        Assert.Equal("本轮没有检查项结果", verdict.MeterLabel);
    }

    [Fact]
    public void All_five_states_are_counted_separately()
    {
        var verdict = RunVerdictBuilder.Build(
        [
            (CheckResultStatus.Ok, (Severity?)null, StatusItemKind.Judgement),
            (CheckResultStatus.Abnormal, Severity.Warning, StatusItemKind.Judgement),
            (CheckResultStatus.Abnormal, Severity.Critical, StatusItemKind.Judgement),
            (CheckResultStatus.Error, (Severity?)null, StatusItemKind.Judgement),
            (CheckResultStatus.InsufficientEvidence, (Severity?)null, StatusItemKind.Informational),
        ]);

        Assert.Equal(5, verdict.Total);
        Assert.Equal(1, verdict.Ok);
        Assert.Equal(1, verdict.Warning);
        Assert.Equal(1, verdict.Danger);
        Assert.Equal(1, verdict.Pending);
        Assert.Equal(1, verdict.Neutral);
        Assert.Equal(2, verdict.Attention);
        Assert.Equal(3, verdict.Judged);
    }

    [Fact]
    public void Error_and_mismatch_like_results_stay_pending_not_danger()
    {
        var verdict = RunVerdictBuilder.Build(
        [
            (CheckResultStatus.Error, Severity.Critical, StatusItemKind.Judgement),
            (CheckResultStatus.InsufficientEvidence, Severity.Critical, StatusItemKind.Judgement),
        ]);

        Assert.Equal(0, verdict.Danger);
        Assert.Equal(2, verdict.Pending);
    }

    [Fact]
    public void Informational_pending_counts_as_neutral()
    {
        var verdict = RunVerdictBuilder.Build(
        [
            (CheckResultStatus.InsufficientEvidence, (Severity?)null, StatusItemKind.Informational),
        ]);

        Assert.Equal(1, verdict.Neutral);
        Assert.Equal(0, verdict.Pending);
        Assert.Contains("中性", verdict.MeterLabel);
    }

    [Fact]
    public void Widths_sum_to_one_hundred_percent()
    {
        var verdict = RunVerdictBuilder.Build(
        [
            (CheckResultStatus.Ok, (Severity?)null, StatusItemKind.Judgement),
            (CheckResultStatus.Ok, (Severity?)null, StatusItemKind.Judgement),
            (CheckResultStatus.Abnormal, Severity.Warning, StatusItemKind.Judgement),
            (CheckResultStatus.Error, (Severity?)null, StatusItemKind.Judgement),
        ]);

        Assert.Equal("width: 50%", verdict.WidthOf(verdict.Ok));
        Assert.Equal("width: 0", RunVerdict.Empty.WidthOf(0));
        Assert.Contains("4 个检查项", verdict.MeterLabel);
    }
}
