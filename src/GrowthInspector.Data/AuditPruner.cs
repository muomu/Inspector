using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Data;

/// <summary>审计留存清理。骨架阶段只保留接线与配置读取，删除逻辑由 TPG-2705 实现。</summary>
public sealed class AuditPruner(
    IOptions<InspectionOptions> options,
    ILogger<AuditPruner> logger) : IAuditPruner
{
    public Task<int> PruneAsync(CancellationToken cancellationToken = default)
    {
        // TODO(TPG-2705): 按 AuditRetentionDays 清理 ChecklistRuns / CheckResults / ToolCallLogs / AiSummaries，
        // 注意 finding 是状态而非历史，不能随时间窗无差别删除。
        logger.LogDebug(
            "审计清理尚未实现（骨架阶段），当前保留期为 {RetentionDays} 天。",
            options.Value.AuditRetentionDays);

        return Task.FromResult(0);
    }
}
