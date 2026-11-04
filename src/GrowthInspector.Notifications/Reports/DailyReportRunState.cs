namespace GrowthInspector.Notifications.Reports;

/// <summary>日报后台服务的进程内运行状态：保证同一自然日只生成 / 发送一次。</summary>
public sealed class DailyReportRunState
{
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public DateOnly? LastCompletedDate { get; set; }
}
