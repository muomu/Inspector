using GrowthInspector.Core.Options;
using GrowthInspector.Orchestration.Queue;
using GrowthInspector.Orchestration.Scheduling;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Web.Scheduling;

/// <summary>
/// 检查单调度器：每个 tick 调 <see cref="ChecklistScheduleScanner"/> 重读库并触发到期的
/// Hourly / Daily 检查单（新建、编辑、停用、归档在下一 tick 即时生效）。
/// </summary>
public sealed class InspectionSchedulerHostedService(
    ChecklistScheduleScanner scanner,
    ChecklistRunStartupGate startupGate,
    IOptions<InspectionOptions> options,
    ILogger<InspectionSchedulerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 等启动清理完成后再扫描，避免把遗留 Running 当成互斥占用而写出 Skipped。
        await startupGate.Completion.WaitAsync(stoppingToken);

        var tickSeconds = Math.Max(5, options.Value.SchedulerTickSeconds);
        logger.LogInformation("巡检调度器已启动，每 {TickSeconds}s 扫描一次频率触发。", tickSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(tickSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await scanner.ScanOnceAsync(stoppingToken);
                if (result.TriggeredPairs > 0)
                {
                    logger.LogInformation(
                        "调度扫描：{TriggeredPairs} 个 (检查单, 环境) 到期并写入后台队列。",
                        result.TriggeredPairs);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 扫描失败不终止调度器，等待下一 tick 重试。
                logger.LogError(ex, "调度扫描失败，等待下一 tick。");
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
    }
}
