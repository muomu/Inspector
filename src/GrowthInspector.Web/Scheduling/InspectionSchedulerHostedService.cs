using GrowthInspector.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Web.Scheduling;

/// <summary>
/// 检查单调度器骨架：保留心跳与配置读取。
/// 真正的「按频率触发 + 快照」由 TPG-2706 实现（每 tick 重新读取启用检查单，保证新建/编辑即时生效）。
/// </summary>
public sealed class InspectionSchedulerHostedService(
    IOptions<InspectionOptions> options,
    ILogger<InspectionSchedulerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tickSeconds = Math.Max(5, options.Value.SchedulerTickSeconds);
        logger.LogInformation(
            "巡检调度器已启动（骨架阶段：仅心跳，周期 {TickSeconds}s；触发逻辑由 TPG-2706 接入）。",
            tickSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(tickSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // TODO(TPG-2706): 查询启用的 Hourly / Daily 检查单与环境，按最后执行时间判断是否到期并调用编排。
            logger.LogDebug("调度心跳 {Timestamp}：到期检查单扫描尚未接入。", DateTimeOffset.Now);
        }
    }
}
