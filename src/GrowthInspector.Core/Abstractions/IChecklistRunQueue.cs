using GrowthInspector.Core.Models;

namespace GrowthInspector.Core.Abstractions;

/// <summary>
/// 检查单执行队列：手动触发与管理 MCP 触发都写入后台队列，不阻塞 Blazor circuit。
/// 同一 (ChecklistId, EnvironmentId) 已在执行时，队列写入一条 RunStatus.Skipped 的 run 并跳过。
/// </summary>
public interface IChecklistRunQueue
{
    ValueTask EnqueueAsync(
        ChecklistTrigger trigger,
        Guid checklistId,
        Guid environmentId,
        CancellationToken cancellationToken = default);
}
