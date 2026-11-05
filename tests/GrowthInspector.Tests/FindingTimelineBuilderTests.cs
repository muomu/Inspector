using GrowthInspector.Core.Models;
using GrowthInspector.Web.Services;

namespace GrowthInspector.Tests;

/// <summary>
/// 异常时间线重建：Finding 不存历史，时间线由检查项的历史执行结果拼出来。
/// 锁住「同轮去重、只看最近 N 轮、正序输出、证据不足算待证」这些边界。
/// </summary>
public sealed class FindingTimelineBuilderTests
{
    [Fact]
    public void Timeline_is_ascending_even_when_input_is_descending()
    {
        var baseTime = DateTimeOffset.UtcNow.AddHours(-5);
        var results = new List<CheckResult>();
        for (var i = 0; i < 3; i++)
        {
            results.Add(Result(baseTime.AddHours(i), CheckResultStatus.Abnormal));
        }

        var timeline = FindingTimelineBuilder.Build(Severity.Warning, results.AsEnumerable().Reverse());

        Assert.Equal(3, timeline.Count);
        Assert.Equal(results.Select(x => x.ChecklistRun!.StartedAt), timeline.Select(x => x.RunStartedAt));
        // Abnormal + Warning 配置级别 → 注意，不给朱红。
        Assert.All(timeline, x => Assert.Equal(StatusState.Warning, x.Presentation.State));
    }

    [Fact]
    public void Same_run_takes_only_latest_result()
    {
        var run = Run(DateTimeOffset.UtcNow.AddHours(-1));
        var earlier = Result(DateTimeOffset.UtcNow.AddMinutes(-90), CheckResultStatus.Abnormal);
        var later = Result(DateTimeOffset.UtcNow.AddMinutes(-60), CheckResultStatus.Ok);
        earlier.ChecklistRun = run;
        later.ChecklistRun = run;
        earlier.ChecklistRunId = run.Id;
        later.ChecklistRunId = run.Id;

        var timeline = FindingTimelineBuilder.Build(Severity.Warning, [earlier, later]);

        var entry = Assert.Single(timeline);
        Assert.Equal(CheckResultStatus.Ok, entry.Status);
        Assert.Equal(run.Id, entry.RunId);
    }

    [Fact]
    public void Caps_to_recent_runs_and_drops_oldest()
    {
        var baseTime = DateTimeOffset.UtcNow.AddDays(-1);
        var results = Enumerable.Range(0, FindingTimelineBuilder.RecentRunLimit + 5)
            .Select(i => Result(baseTime.AddMinutes(i), CheckResultStatus.Abnormal))
            .ToList();

        var timeline = FindingTimelineBuilder.Build(Severity.Warning, results);

        Assert.Equal(FindingTimelineBuilder.RecentRunLimit, timeline.Count);
        // 最老的 5 轮被丢掉：时间线从第 6 轮开始。
        Assert.Equal(results[5].ChecklistRun!.StartedAt, timeline[0].RunStartedAt);
    }

    [Fact]
    public void Results_without_run_are_ignored()
    {
        var orphan = Result(DateTimeOffset.UtcNow, CheckResultStatus.Abnormal);
        orphan.ChecklistRun = null;

        var timeline = FindingTimelineBuilder.Build(Severity.Warning, [orphan]);

        Assert.Empty(timeline);
    }

    [Fact]
    public void Detail_prefers_narrative_and_falls_back_to_error()
    {
        var withNarrative = Result(DateTimeOffset.UtcNow.AddHours(-2), CheckResultStatus.Abnormal);
        withNarrative.Narrative = "端口 8080 无监听";
        var withError = Result(DateTimeOffset.UtcNow.AddHours(-1), CheckResultStatus.Error);
        withError.ErrorMessage = "MCP 调用失败";

        var timeline = FindingTimelineBuilder.Build(Severity.Warning, [withNarrative, withError]);

        Assert.Equal("端口 8080 无监听", timeline[0].Detail);
        Assert.Equal("MCP 调用失败", timeline[1].Detail);
    }

    [Fact]
    public void Insufficient_evidence_shows_as_pending()
    {
        var insufficient = Result(DateTimeOffset.UtcNow, CheckResultStatus.InsufficientEvidence);

        var timeline = FindingTimelineBuilder.Build(Severity.Warning, [insufficient]);

        // 证据不足不是故障也不算信息：统一「待证」。
        Assert.Equal(StatusState.Pending, timeline.Single().Presentation.State);
    }

    [Fact]
    public void Missing_item_severity_falls_back_to_finding_severity_for_abnormal()
    {
        var abnormal = Result(DateTimeOffset.UtcNow, CheckResultStatus.Abnormal);

        var timeline = FindingTimelineBuilder.Build(Severity.Critical, [abnormal]);

        Assert.Equal(StatusState.Danger, timeline.Single().Presentation.State);
    }

    private static ChecklistRun Run(DateTimeOffset startedAt) => new()
    {
        StartedAt = startedAt,
        Status = RunStatus.Succeeded,
    };

    private static CheckResult Result(DateTimeOffset runStartedAt, CheckResultStatus status)
    {
        var run = Run(runStartedAt);
        return new CheckResult
        {
            ChecklistRun = run,
            ChecklistRunId = run.Id,
            Status = status,
            CreatedAt = runStartedAt.AddMinutes(1),
        };
    }
}
