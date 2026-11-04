using System.Threading.Channels;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GrowthInspector.Management;

/// <summary>
/// 检查单执行队列的最小实现：手动触发与管理 MCP 触发都只写内存通道，由后台服务串行消费。
/// TPG-2706 接入完整调度（并发 / 互斥 / 启动清理）后，<c>TryAdd</c> 注册会优先保留那份实现。
/// </summary>
internal sealed class ChecklistRunQueue : IChecklistRunQueue
{
    private readonly Channel<RunRequest> channel = Channel.CreateUnbounded<RunRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    internal ChannelReader<RunRequest> Reader => channel.Reader;

    public ValueTask EnqueueAsync(
        ChecklistTrigger trigger,
        Guid checklistId,
        Guid environmentId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        channel.Writer.TryWrite(new RunRequest(trigger, checklistId, environmentId));
        return ValueTask.CompletedTask;
    }

    internal sealed record RunRequest(ChecklistTrigger Trigger, Guid ChecklistId, Guid EnvironmentId);
}

/// <summary>消费 <see cref="ChecklistRunQueue"/>：每个请求创建独立 scope 调编排入口，单项失败只记日志。</summary>
internal sealed class ChecklistRunQueueHostedService(
    ChecklistRunQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<ChecklistRunQueueHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<IInspectionOrchestrator>();
                await orchestrator.RunAsync(request.ChecklistId, request.EnvironmentId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "后台执行检查单 {ChecklistId}（环境 {EnvironmentId}，触发 {Trigger}）失败。",
                    request.ChecklistId,
                    request.EnvironmentId,
                    request.Trigger);
            }
        }
    }
}
