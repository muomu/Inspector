namespace GrowthInspector.Orchestration.Queue;

/// <summary>
/// 启动清理完成的信号：队列 worker 与调度扫描在清理完成前不工作，避免把遗留 Running
/// 误判成新触发需要互斥的轮次，或让调度扫描抢在清理之前读库。
/// 清理失败也会置位（失败不阻塞应用启动，遗留执行在后续启动继续清理）。
/// </summary>
public sealed class ChecklistRunStartupGate
{
    private readonly TaskCompletionSource completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Completion => completion.Task;

    public void MarkCompleted() => completion.TrySetResult();
}
