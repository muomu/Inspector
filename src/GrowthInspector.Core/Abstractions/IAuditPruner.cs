namespace GrowthInspector.Core.Abstractions;

/// <summary>审计留存清理：按配置的保留期删除过期的执行历史 / 工具调用 / 快照 / 摘要。</summary>
public interface IAuditPruner
{
    /// <summary>清理一次，返回删除的记录数。</summary>
    Task<int> PruneAsync(CancellationToken cancellationToken = default);
}
