using System.Globalization;
using GrowthInspector.Core.Options;
using GrowthInspector.Notifications.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Notifications.Reports;

/// <summary>
/// 日报触发判定（TPG-2714）：按 InspectionOptions.TimeZone 折算本地时间，
/// 达到 DailyScheduleTime 且当日尚未完成时触发一次；失败不记账，下一 tick 重试。
/// </summary>
public sealed class DailyReportScheduler(
    DailyReportRunner runner,
    DailyReportRunState runState,
    IOptions<InspectionOptions> inspectionOptions,
    TimeProvider timeProvider,
    ILogger<DailyReportScheduler> logger)
{
    /// <summary>触发一次检查；返回是否真的生成了日报。</summary>
    public async Task<bool> TickAsync(CancellationToken cancellationToken = default)
    {
        var options = inspectionOptions.Value;
        var timeZone = InspectionTimeZone.Resolve(options.TimeZone);
        var localNow = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), timeZone);
        var localDate = DateOnly.FromDateTime(localNow.DateTime);
        var scheduledTime = ParseDailyScheduleTime(options.DailyScheduleTime);

        if (TimeOnly.FromDateTime(localNow.DateTime) < scheduledTime)
        {
            return false;
        }

        await runState.Gate.WaitAsync(cancellationToken);
        try
        {
            if (runState.LastCompletedDate == localDate)
            {
                return false;
            }

            logger.LogInformation(
                "开始生成 {ReportDate} 巡检日报（本地时间 {LocalNow}，触发时刻 {ScheduledTime}，时区 {TimeZone}）。",
                localDate,
                localNow,
                scheduledTime,
                timeZone.Id);

            var results = await runner.RunAsync(localDate, cancellationToken);
            runState.LastCompletedDate = localDate;

            logger.LogInformation(
                "巡检日报处理完成：{ProjectCount} 个 Project，发送 {SentCount} 条，跳过空日报 {SkippedCount} 条。",
                results.Count,
                results.Count(x => x.Sent),
                results.Count(x => x.SkippedEmpty));
            return true;
        }
        finally
        {
            runState.Gate.Release();
        }
    }

    /// <summary>解析 HH:mm；配置缺失 / 非法时回落到 10:00。</summary>
    public static TimeOnly ParseDailyScheduleTime(string? value) =>
        TimeOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : new TimeOnly(10, 0);
}
