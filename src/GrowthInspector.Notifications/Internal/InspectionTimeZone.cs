namespace GrowthInspector.Notifications.Internal;

/// <summary>
/// 巡检时区工具：日报日期边界与通知时间展示统一走 InspectionOptions.TimeZone，
/// 配置缺失或非法时回落到 UTC，不因时区配置错误阻断通知链路。
/// </summary>
internal static class InspectionTimeZone
{
    public static TimeZoneInfo Resolve(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>取本地自然日的 UTC 起止区间（左闭右开）。</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) GetDayRange(DateOnly date, TimeZoneInfo timeZone)
    {
        var startLocal = date.ToDateTime(TimeOnly.MinValue);
        var endLocal = date.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return (ToOffset(startLocal, timeZone), ToOffset(endLocal, timeZone));
    }

    /// <summary>按配置时区格式化为分钟级时间文本（不含时区后缀）。</summary>
    public static string Format(DateTimeOffset value, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTime(value, timeZone).ToString("yyyy-MM-dd HH:mm");

    private static DateTimeOffset ToOffset(DateTime local, TimeZoneInfo timeZone) =>
        new(local, timeZone.GetUtcOffset(local));
}
