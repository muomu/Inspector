using GrowthInspector.Core.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Orchestration.Queue;

/// <summary>
/// 队列消费 worker：按 MaxConcurrentChecklists 启动固定数量的消费者，每个消费者从 Channel
/// 取一轮执行并在自己的 DI scope 内调用执行器。检查单之间并行、检查单内串行由执行器保证。
/// </summary>
public sealed class ChecklistRunQueueWorker(
    ChecklistRunQueue queue,
    ChecklistRunStartupGate startupGate,
    IServiceScopeFactory scopeFactory,
    IOptions<InspectionOptions> options,
    ILogger<ChecklistRunQueueWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await startupGate.Completion.WaitAsync(stoppingToken);

        var concurrency = Math.Max(1, options.Value.MaxConcurrentChecklists);
        logger.LogInformation(
            "检查单执行队列 worker 已启动（并发上限 {Concurrency}，tick 间隔 {TickSeconds}s）。",
            concurrency,
            options.Value.SchedulerTickSeconds);

        var consumers = new Task[concurrency];
        for (var index = 0; index < concurrency; index++)
        {
            consumers[index] = ConsumeAsync(stoppingToken);
        }

        await Task.WhenAll(consumers);
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var runId in queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var executor = scope.ServiceProvider.GetRequiredService<IChecklistRunExecutor>();
                    await executor.ExecuteRunAsync(runId, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 单轮执行不得拖垮 worker；执行器内部已把可恢复错误落到 Failed / CheckResult.Error。
                    logger.LogError(ex, "消费检查单执行 {RunId} 时发生未捕获异常。", runId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 应用正常停机。
        }
    }
}
