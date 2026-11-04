using GrowthInspector.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Data;

/// <summary>
/// 定时执行审计清理。<see cref="AuditPruner"/> 本身是 scoped 服务，
/// 这里每个周期开一个 scope 解析，避免持有长生命周期的 DbContext。
/// 单次清理失败只记录日志，不影响进程与后续周期。
/// </summary>
public sealed class AuditPruningHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<AuditPruningOptions> options,
    ILogger<AuditPruningHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var schedule = options.Value;
        var interval = schedule.Interval > TimeSpan.Zero ? schedule.Interval : TimeSpan.FromHours(24);
        var initialDelay = schedule.InitialDelay > TimeSpan.Zero ? schedule.InitialDelay : TimeSpan.Zero;

        try
        {
            if (initialDelay > TimeSpan.Zero)
            {
                await Task.Delay(initialDelay, stoppingToken);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var pruner = scope.ServiceProvider.GetRequiredService<IAuditPruner>();
                    await pruner.PruneAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "审计清理失败，将在 {Interval} 后重试。", interval);
                }

                await Task.Delay(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 进程停止：正常退出。
        }
    }
}
