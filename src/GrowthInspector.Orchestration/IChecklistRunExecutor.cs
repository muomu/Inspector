namespace GrowthInspector.Orchestration;

/// <summary>
/// 执行一条已经创建好的检查单执行记录（Pending）：读取快照、按 SortOrder 串行执行检查项、
/// 每项立即落库、收尾。由后台队列 worker 在独立 DI scope 内调用。
/// </summary>
public interface IChecklistRunExecutor
{
    Task ExecuteRunAsync(Guid checklistRunId, CancellationToken cancellationToken = default);
}
