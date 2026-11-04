using GrowthInspector.Core.Models;
using GrowthInspector.Web.Services;

namespace GrowthInspector.Tests;

/// <summary>状态展示映射：页面只消费 StatusPresenter，测试锁住五态 + Running 的文案与语义。</summary>
public sealed class StatusPresenterTests
{
    [Fact]
    public void Ok_result_maps_to_ok_state()
    {
        var status = StatusPresenter.FromResult(CheckResultStatus.Ok);

        Assert.Equal(StatusState.Ok, status.State);
        Assert.Equal("Ok", status.Label);
        Assert.Equal("✓", status.Glyph);
        Assert.Equal("status status--ok", status.CssClass);
    }

    [Fact]
    public void Abnormal_warning_maps_to_warning()
    {
        var status = StatusPresenter.FromResult(CheckResultStatus.Abnormal, Severity.Warning);

        Assert.Equal(StatusState.Warning, status.State);
        Assert.Equal("注意", status.Label);
        Assert.Equal("▲", status.Glyph);
        Assert.Equal("status status--warn", status.CssClass);
    }

    [Fact]
    public void Abnormal_critical_maps_to_danger()
    {
        var status = StatusPresenter.FromResult(CheckResultStatus.Abnormal, Severity.Critical);

        Assert.Equal(StatusState.Danger, status.State);
        Assert.Equal("异常", status.Label);
        Assert.Equal("■", status.Glyph);
        Assert.Equal("status status--bad", status.CssClass);
        Assert.Equal("node--bad", status.NodeModifierClass);
    }

    [Fact]
    public void Abnormal_info_stays_neutral()
    {
        var status = StatusPresenter.FromResult(CheckResultStatus.Abnormal, Severity.Info);

        Assert.Equal(StatusState.Neutral, status.State);
        Assert.Contains("Abnormal", status.Sub);
        Assert.DoesNotContain("--bad", status.CssClass);
    }

    [Fact]
    public void Abnormal_without_severity_is_pending_not_info()
    {
        var status = StatusPresenter.FromResult(CheckResultStatus.Abnormal, severity: null);

        Assert.Equal(StatusState.Pending, status.State);
        Assert.Equal("待证", status.Label);
        Assert.Contains("严重级别", status.Sub);
        Assert.Equal("status status--pending", status.CssClass);
    }

    [Fact]
    public void Missing_result_is_pending()
    {
        var status = StatusPresenter.MissingResult();

        Assert.Equal(StatusState.Pending, status.State);
        Assert.Equal("无结果", status.Sub);
        Assert.Equal("status status--pending", status.CssClass);
    }

    [Fact]
    public void Judgement_insufficient_evidence_maps_to_pending_not_danger()
    {
        var status = StatusPresenter.FromResult(
            CheckResultStatus.InsufficientEvidence,
            Severity.Warning,
            StatusItemKind.Judgement);

        Assert.Equal(StatusState.Pending, status.State);
        Assert.Equal("待证", status.Label);
        Assert.Equal("status status--pending", status.CssClass);
        Assert.DoesNotContain("--bad", status.CssClass);
    }

    [Fact]
    public void Informational_insufficient_evidence_maps_to_neutral_info()
    {
        var status = StatusPresenter.FromResult(
            CheckResultStatus.InsufficientEvidence,
            null,
            StatusItemKind.Informational);

        Assert.Equal(StatusState.Neutral, status.State);
        Assert.Equal("信息", status.Label);
    }

    [Fact]
    public void Error_maps_to_pending_without_danger_color()
    {
        var status = StatusPresenter.FromResult(CheckResultStatus.Error);

        Assert.Equal(StatusState.Pending, status.State);
        Assert.Equal("待证", status.Label);
        Assert.Equal("Error", status.Sub);
        Assert.DoesNotContain("--bad", status.CssClass);
    }

    [Fact]
    public void Succeeded_run_is_done_not_healthy()
    {
        var status = StatusPresenter.FromRun(RunStatus.Succeeded);

        Assert.Equal(StatusState.Neutral, status.State);
        Assert.Equal("完成", status.Label);
        Assert.DoesNotContain("成功", status.Label);
        Assert.Contains("不代表健康", status.AriaLabel);
        Assert.Equal("status status--idle", status.CssClass);
    }

    [Fact]
    public void Run_statuses_cover_running_and_failed()
    {
        var running = StatusPresenter.FromRun(RunStatus.Running);
        var failed = StatusPresenter.FromRun(RunStatus.Failed);
        var skipped = StatusPresenter.FromRun(RunStatus.Skipped);

        Assert.Equal(StatusState.Running, running.State);
        Assert.Equal("status status--running", running.CssClass);
        Assert.Equal(StatusState.Danger, failed.State);
        Assert.Equal("status status--bad", failed.CssClass);
        Assert.Equal(StatusState.Neutral, skipped.State);
    }

    [Fact]
    public void Finding_chips_distinguish_open_and_resolved()
    {
        var open = StatusPresenter.FromFinding(FindingStatus.Open);
        var resolved = StatusPresenter.FromFinding(FindingStatus.Resolved);

        Assert.Equal(StatusPresentationMode.Chip, open.Mode);
        Assert.Equal("未恢复", open.Label);
        Assert.Equal("chip", open.CssClass);
        Assert.Equal("已恢复", resolved.Label);
        Assert.Equal("检查恢复", resolved.Sub);
        Assert.Equal("chip chip--ok", resolved.CssClass);
    }

    [Fact]
    public void Resolved_finding_explains_rule_change()
    {
        var resolved = StatusPresenter.FromFinding(FindingStatus.Resolved, FindingCloseReason.RuleChanged);

        Assert.Equal("判据变更", resolved.Sub);
        Assert.Contains("判据变更", resolved.AriaLabel);
    }

    [Fact]
    public void Pending_and_canceled_runs_stay_neutral()
    {
        Assert.Equal(StatusState.Neutral, StatusPresenter.FromRun(RunStatus.Pending).State);
        Assert.Equal(StatusState.Neutral, StatusPresenter.FromRun(RunStatus.Canceled).State);
        Assert.Equal(StatusState.Neutral, StatusPresenter.FromRun(RunStatus.Skipped).State);
    }

    [Fact]
    public void Severity_labels_stay_separate_from_status_words()
    {
        Assert.Equal("Critical", StatusPresenter.FromSeverity(Severity.Critical).Label);
        Assert.Equal("Warning", StatusPresenter.FromSeverity(Severity.Warning).Label);
        Assert.Equal("Info", StatusPresenter.FromSeverity(Severity.Info).Label);
    }
}
