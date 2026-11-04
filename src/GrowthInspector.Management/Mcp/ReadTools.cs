using System.ComponentModel;
using GrowthInspector.Core.Models;
using ModelContextProtocol.Server;

namespace GrowthInspector.Management.Mcp;

/// <summary>
/// 管理 MCP 只读工具面：项目 / 环境 / MCP server / 手册 / 检查单 / 执行 / 结果 / 异常 / 摘要 / 工具调用。
/// 每个工具都强制 limit + cursor 分页；检查项结果同时返回 agent 判定与框架复核判定。
/// </summary>
[McpServerToolType]
public sealed class ReadTools(IManagementService management)
{
    private readonly IManagementService management = management;

    // ---- 项目 / 环境 / MCP server ----

    [McpServerTool(Name = "project_list", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("分页列出项目。默认过滤归档。")]
    public Task<string> ListProjectsAsync(
        int? limit = null,
        string? cursor = null,
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => await management.ListProjectsAsync(
            includeArchived,
            limit ?? 50,
            cursor,
            cancellationToken));

    [McpServerTool(Name = "project_get", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("按 id 获取项目详情；Lark webhook 以掩码返回。")]
    public Task<string> GetProjectAsync(string id, CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => await management.GetProjectAsync(
            ToolResponse.ParseId(id),
            cancellationToken));

    [McpServerTool(Name = "environment_list", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("分页列出环境，可按项目过滤；默认过滤归档。")]
    public Task<string> ListEnvironmentsAsync(
        string? projectId = null,
        int? limit = null,
        string? cursor = null,
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => await management.ListEnvironmentsAsync(
            ToolResponse.ParseOptionalId(projectId, "projectId"),
            includeArchived,
            limit ?? 50,
            cursor,
            cancellationToken));

    [McpServerTool(Name = "environment_get", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("按 id 获取环境。")]
    public Task<string> GetEnvironmentAsync(string id, CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => await management.GetEnvironmentAsync(
            ToolResponse.ParseId(id),
            cancellationToken));

    [McpServerTool(Name = "mcp_server_list", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("分页列出 MCP server，可按项目过滤；命令 / 参数 / URL 以掩码返回。")]
    public Task<string> ListMcpServersAsync(
        string? projectId = null,
        int? limit = null,
        string? cursor = null,
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => await management.ListMcpServersAsync(
            ToolResponse.ParseOptionalId(projectId, "projectId"),
            includeArchived,
            limit ?? 50,
            cursor,
            cancellationToken));

    [McpServerTool(Name = "mcp_server_get", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("按 id 获取 MCP server；命令 / 参数 / URL 以掩码返回。")]
    public Task<string> GetMcpServerAsync(string id, CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => await management.GetMcpServerAsync(
            ToolResponse.ParseId(id),
            cancellationToken));

    // ---- 手册 ----

    [McpServerTool(Name = "runbook_list", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("分页列出手册，可按项目与 Kind（General/Specialized）过滤；默认过滤归档。")]
    public Task<string> ListRunbooksAsync(
        string? projectId = null,
        string? kind = null,
        int? limit = null,
        string? cursor = null,
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => await management.ListRunbooksAsync(
            ToolResponse.ParseOptionalId(projectId, "projectId"),
            ToolResponse.ParseOptionalEnum<RunbookKind>(kind, "kind"),
            includeArchived,
            limit ?? 50,
            cursor,
            cancellationToken));

    [McpServerTool(Name = "runbook_get", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("按 id 获取手册全文。")]
    public Task<string> GetRunbookAsync(string id, CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => await management.GetRunbookAsync(
            ToolResponse.ParseId(id),
            cancellationToken));

    // ---- 检查单 / 检查项 / 引用 ----

    [McpServerTool(Name = "checklist_list", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("分页列出检查单，可按项目过滤；默认过滤归档。")]
    public Task<string> ListChecklistsAsync(
        string? projectId = null,
        int? limit = null,
        string? cursor = null,
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => await management.ListChecklistsAsync(
            ToolResponse.ParseOptionalId(projectId, "projectId"),
            includeArchived,
            limit ?? 50,
            cursor,
            cancellationToken));

    [McpServerTool(Name = "checklist_get", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("按 id 获取检查单（含检查项数、引用手册数与最近运行时间）。")]
    public Task<string> GetChecklistAsync(string id, CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => await management.GetChecklistAsync(
            ToolResponse.ParseId(id),
            cancellationToken));

    [McpServerTool(Name = "checklist_item_list", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("分页列出检查单下的检查项（含结构化断言）；默认过滤归档。")]
    public Task<string> ListChecklistItemsAsync(
        string checklistId,
        int? limit = null,
        string? cursor = null,
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => await management.ListChecklistItemsAsync(
            ToolResponse.ParseId(checklistId, "checklistId"),
            includeArchived,
            limit ?? 200,
            cursor,
            cancellationToken));

    [McpServerTool(Name = "checklist_item_get", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("按 id 获取检查项。")]
    public Task<string> GetChecklistItemAsync(string id, CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => await management.GetChecklistItemAsync(
            ToolResponse.ParseId(id),
            cancellationToken));

    [McpServerTool(Name = "checklist_runbook_list", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("分页列出检查单引用的专用手册。")]
    public Task<string> ListChecklistRunbookRefsAsync(
        string checklistId,
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => await management.ListChecklistRunbookRefsAsync(
            ToolResponse.ParseId(checklistId, "checklistId"),
            limit ?? 200,
            cursor,
            cancellationToken));

    // ---- 执行 / 结果 / 异常 / 摘要 / 工具调用 ----

    [McpServerTool(Name = "run_list", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("分页列出执行记录，可按检查单、环境与状态过滤。")]
    public Task<string> ListRunsAsync(
        string? checklistId = null,
        string? environmentId = null,
        string? status = null,
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => await management.ListRunsAsync(
            ToolResponse.ParseOptionalId(checklistId, "checklistId"),
            ToolResponse.ParseOptionalId(environmentId, "environmentId"),
            ToolResponse.ParseOptionalEnum<RunStatus>(status, "status"),
            limit ?? 50,
            cursor,
            cancellationToken));

    [McpServerTool(Name = "run_get", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("按 id 获取执行记录。")]
    public Task<string> GetRunAsync(string id, CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => await management.GetRunAsync(
            ToolResponse.ParseId(id),
            cancellationToken));

    [McpServerTool(Name = "check_result_list", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("分页列出某轮执行的检查项结果；每条同时包含 agent 判定与框架复核判定。")]
    public Task<string> ListCheckResultsAsync(
        string runId,
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => await management.ListCheckResultsAsync(
            ToolResponse.ParseId(runId, "runId"),
            limit ?? 200,
            cursor,
            cancellationToken));

    [McpServerTool(Name = "check_result_get", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("按 id 获取检查项结果（双判与证据）。")]
    public Task<string> GetCheckResultAsync(string id, CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => await management.GetCheckResultAsync(
            ToolResponse.ParseId(id),
            cancellationToken));

    [McpServerTool(Name = "finding_list", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("分页列出异常项，可按项目、环境与状态（Open/Resolved）过滤。")]
    public Task<string> ListFindingsAsync(
        string? projectId = null,
        string? environmentId = null,
        string? status = null,
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => await management.ListFindingsAsync(
            ToolResponse.ParseOptionalId(projectId, "projectId"),
            ToolResponse.ParseOptionalId(environmentId, "environmentId"),
            ToolResponse.ParseOptionalEnum<FindingStatus>(status, "status"),
            limit ?? 50,
            cursor,
            cancellationToken));

    [McpServerTool(Name = "finding_get", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("按 id 获取异常项。")]
    public Task<string> GetFindingAsync(string id, CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => await management.GetFindingAsync(
            ToolResponse.ParseId(id),
            cancellationToken));

    [McpServerTool(Name = "run_summary_get", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("获取某轮执行的 AI 摘要；没有摘要时 data 为 null。")]
    public Task<string> GetRunSummaryAsync(string runId, CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => await management.GetRunSummaryAsync(
            ToolResponse.ParseId(runId, "runId"),
            cancellationToken));

    [McpServerTool(Name = "daily_report_list", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("分页列出日报，可按日期（yyyy-MM-dd）过滤。")]
    public Task<string> ListDailyReportsAsync(
        string? reportDate = null,
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => await management.ListDailyReportsAsync(
            ToolResponse.ParseOptionalDate(reportDate),
            limit ?? 31,
            cursor,
            cancellationToken));

    [McpServerTool(Name = "tool_call_list", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("分页列出某轮执行的工具调用审计，可按检查项过滤。")]
    public Task<string> ListToolCallsAsync(
        string runId,
        string? checklistItemId = null,
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => await management.ListToolCallsAsync(
            ToolResponse.ParseId(runId, "runId"),
            ToolResponse.ParseOptionalId(checklistItemId, "checklistItemId"),
            limit ?? 100,
            cursor,
            cancellationToken));
}
