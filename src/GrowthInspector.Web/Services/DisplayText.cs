using GrowthInspector.Core.Models;

namespace GrowthInspector.Web.Services;

/// <summary>
/// 跨页共用的展示文案：只做「怎么把字段写成一句话」，
/// 不做状态映射（状态一律走 <see cref="StatusPresenter"/>）。
/// 页面上不允许再复制一份 NameOrMissing / FormatDuration / TriggerText。
/// </summary>
public static class DisplayText
{
    public const string MissingName = "(已归档/已删除)";

    public static string NameOrMissing(string? name)
        => string.IsNullOrWhiteSpace(name) ? MissingName : name;

    public static string OrDash(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value;

    public static string Shorten(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        return value.Length <= maxLength ? value : $"{value[..maxLength]}…";
    }

    /// <summary>耗时：&lt;1s 用毫秒，否则保留一位小数秒；空值 / 0 显示「-」。</summary>
    public static string FormatDuration(long milliseconds) => milliseconds switch
    {
        <= 0 => "-",
        < 1000 => $"{milliseconds} ms",
        _ => $"{milliseconds / 1000d:0.0} s",
    };

    public static string TriggerText(ChecklistTrigger trigger)
        => trigger == ChecklistTrigger.Schedule ? "调度" : "手动";

    public static string FrequencyText(ChecklistFrequency frequency) => frequency switch
    {
        ChecklistFrequency.Manual => "手动",
        ChecklistFrequency.Hourly => "每小时",
        ChecklistFrequency.Daily => "每天",
        _ => frequency.ToString(),
    };

    public static string RunbookKindText(RunbookKind kind)
        => kind == RunbookKind.General ? "通用" : "专用";

    public static string CloseReasonText(FindingCloseReason reason) => reason switch
    {
        FindingCloseReason.Recovered => "检查恢复",
        _ => reason.ToString(),
    };

    /// <summary>归档标记统一为 chip 文案；不归档时返回 null，调用方按需渲染。</summary>
    public static string? ArchivedLabel(bool archived) => archived ? "已归档" : null;

    /// <summary>列表里给已归档名称补一个可见后缀（表格里空间不足时用）。</summary>
    public static string WithArchived(string name, bool archived)
        => archived ? $"{name}（已归档）" : name;
}
