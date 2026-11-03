namespace GrowthInspector.Core.Abstractions;

/// <summary>检查单执行编排入口：由调度器或手动触发调用，返回本轮执行 Id。</summary>
public interface IInspectionOrchestrator
{
    Task<Guid> RunAsync(Guid checklistId, Guid environmentId, CancellationToken cancellationToken = default);
}
