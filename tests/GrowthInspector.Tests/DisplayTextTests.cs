using GrowthInspector.Core.Models;
using GrowthInspector.Web.Services;

namespace GrowthInspector.Tests;

/// <summary>跨页展示文案：锁住格式，避免各页复制出不一致的「已归档 / 耗时 / 触发方式」。</summary>
public sealed class DisplayTextTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NameOrMissing_falls_back_for_blank_names(string? name)
        => Assert.Equal(DisplayText.MissingName, DisplayText.NameOrMissing(name));

    [Fact]
    public void NameOrMissing_keeps_real_name()
        => Assert.Equal("core 巡检", DisplayText.NameOrMissing("core 巡检"));

    [Theory]
    [InlineData(0, "-")]
    [InlineData(-5, "-")]
    [InlineData(12, "12 ms")]
    [InlineData(999, "999 ms")]
    [InlineData(1000, "1.0 s")]
    [InlineData(2345, "2.3 s")]
    public void FormatDuration_uses_ms_below_one_second(long milliseconds, string expected)
        => Assert.Equal(expected, DisplayText.FormatDuration(milliseconds));

    [Fact]
    public void Shorten_returns_dash_for_blank_and_ellipsis_for_long()
    {
        Assert.Equal("-", DisplayText.Shorten(null, 10));
        Assert.Equal("abcdefghij", DisplayText.Shorten("abcdefghij", 10));
        Assert.Equal("abcde…", DisplayText.Shorten("abcdefghij", 5));
    }

    [Theory]
    [InlineData(ChecklistTrigger.Manual, "手动")]
    [InlineData(ChecklistTrigger.Schedule, "调度")]
    public void TriggerText_is_stable(ChecklistTrigger trigger, string expected)
        => Assert.Equal(expected, DisplayText.TriggerText(trigger));

    [Theory]
    [InlineData(ChecklistFrequency.Manual, "手动")]
    [InlineData(ChecklistFrequency.Hourly, "每小时")]
    [InlineData(ChecklistFrequency.Daily, "每天")]
    public void FrequencyText_is_stable(ChecklistFrequency frequency, string expected)
        => Assert.Equal(expected, DisplayText.FrequencyText(frequency));

    [Theory]
    [InlineData(RunbookKind.General, "通用")]
    [InlineData(RunbookKind.Specialized, "专用")]
    public void RunbookKindText_is_stable(RunbookKind kind, string expected)
        => Assert.Equal(expected, DisplayText.RunbookKindText(kind));

    [Theory]
    [InlineData(FindingCloseReason.Recovered, "检查恢复")]
    public void CloseReasonText_is_stable(FindingCloseReason reason, string expected)
        => Assert.Equal(expected, DisplayText.CloseReasonText(reason));

    [Fact]
    public void Archived_label_only_when_archived()
    {
        Assert.Null(DisplayText.ArchivedLabel(false));
        Assert.Equal("已归档", DisplayText.ArchivedLabel(true));
        Assert.Equal("手工手册（已归档）", DisplayText.WithArchived("手工手册", true));
        Assert.Equal("手工手册", DisplayText.WithArchived("手工手册", false));
    }
}
