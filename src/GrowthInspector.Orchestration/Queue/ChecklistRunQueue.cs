using System.Threading.Channels;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Orchestration.Scheduling;
using Microsoft.Extensions.Logging;

namespace GrowthInspector.Orchestration.Queue;

/// <summary>
/// 进程内检查单执行队列：EnqueueAsync 只做「创建一轮执行（快照 + 互斥）并写入 Channel」，
/// 由 <see cref="ChecklistRunQueueWorker"/> 在后台消费，不阻塞 Blazor circuit。
/// </summary>
public sealed class ChecklistRunQueue(
    ChecklistRunFactory runFactory,
    ILogger<ChecklistRunQueue> logger) : IChecklistRunQueue
{
    private readonly Channel<Guid> channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });

    internal ChannelReader<Guid> Reader => channel.Reader;

    /// <summary>
    /// 创建一轮执行并写入队列，返回执行记录 Id。若同一 (检查单, 环境) 已有待执行 / 执行中的轮次，
    /// 写入一条 Skipped 执行记录并直接返回，不进入队列。
    /// </summary>
    public async Task<Guid> EnqueueRunAsync(
        ChecklistTrigger trigger,
        Guid checklistId,
        Guid environmentId,
        CancellationToken cancellationToken = default)
    {
        var creation = await runFactory.CreateAsync(trigger, checklistId, environmentId, cancellationToken);
        if (!creation.Skipped)
        {
            await channel.Writer.WriteAsync(creation.RunId, cancellationToken);
            logger.LogInformation(
                "执行 {RunId} 已写入后台队列（checklist={ChecklistId}, environment={EnvironmentId}, trigger={Trigger}）。",
                creation.RunId,
                checklistId,
                environmentId,
                trigger);
        }

        return creation.RunId;
    }

    /// <inheritdoc />
    public async ValueTask EnqueueAsync(
        ChecklistTrigger trigger,
        Guid checklistId,
        Guid environmentId,
        CancellationToken cancellationToken = default)
        => await EnqueueRunAsync(trigger, checklistId, environmentId, cancellationToken);
}
