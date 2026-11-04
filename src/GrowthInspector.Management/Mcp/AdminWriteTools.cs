using System.ComponentModel;
using ModelContextProtocol.Server;

namespace GrowthInspector.Management.Mcp;

/// <summary>
/// 管理 MCP 管理写工具面（admin-write 档）：触发检查单运行。
/// 只把请求写入后台队列，不在 MCP 请求里同步执行检查。
/// </summary>
[McpServerToolType]
public sealed class AdminWriteTools(IManagementService management)
{
    private readonly IManagementService management = management;

    [McpServerTool(Name = "checklist_trigger", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("手动触发一张检查单在指定环境执行；写入后台队列后立即返回，不等待执行完成。")]
    public Task<string> TriggerChecklistAsync(
        string checklistId,
        string environmentId,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () =>
        {
            var result = await management.EnqueueChecklistRunAsync(
                ToolResponse.ParseId(checklistId, "checklistId"),
                ToolResponse.ParseId(environmentId, "environmentId"),
                Core.Models.ChecklistTrigger.Manual,
                cancellationToken);

            return new
            {
                result.ChecklistId,
                result.EnvironmentId,
                trigger = result.Trigger.ToString(),
            };
        });
}
