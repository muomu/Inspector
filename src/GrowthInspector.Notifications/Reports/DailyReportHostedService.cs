using GrowthInspector.Core.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Notifications.Reports;

/// <summary>
/// 日报后台服务（TPG-2714）：按 SchedulerTickSeconds 轮询，注入 TimeProvider 便于测试；
/// 每个 tick 创建独立 scope 解析 scoped 的日报依赖，避免 BackgroundService 捕获 scoped 服务。
/// </summary>
public sealed class DailyReportHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<InspectionOptions> inspectionOptions,
    ILogger<DailyReportHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = inspectionOptions.Value;
        var tickSeconds = Math.Max(30, options.SchedulerTickSeconds);

        logger.LogInformation(
            "巡检日报后台服务已启动（每日 {DailyScheduleTime}，时区 {TimeZone}，tick {TickSeconds}s）。",
            options.DailyScheduleTime,
            options.TimeZone,
            tickSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(tickSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var scheduler = scope.ServiceProvider.GetRequiredService<DailyReportScheduler>();
                await scheduler.TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 日报失败不应终止后台服务，下一 tick 重试；成功后才记账，避免重复发送。
                logger.LogError(ex, "巡检日报 tick 失败，等待下一轮重试。");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("巡检日报后台服务已停止。");
    }
}
