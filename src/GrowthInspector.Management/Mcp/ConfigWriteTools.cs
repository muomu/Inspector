using System.ComponentModel;
using GrowthInspector.Core.Models;
using ModelContextProtocol.Server;

namespace GrowthInspector.Management.Mcp;

/// <summary>
/// 管理 MCP 配置写工具面（config-write 档）：项目 / 环境 / MCP server / 手册 / 检查单 / 检查项 / 引用。
/// 凭据字段传掩码或空值表示保持原值；写操作不直接执行检查单。
/// </summary>
[McpServerToolType]
public sealed class ConfigWriteTools(IManagementService management)
{
    private readonly IManagementService management = management;

    // ---- 项目 ----

    [McpServerTool(Name = "project_create", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("创建项目。")]
    public Task<string> CreateProjectAsync(
        string name,
        string? code = null,
        string? larkWebhookUrl = null,
        string? opsContext = null,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => new
        {
            id = await management.CreateProjectAsync(
                new CreateProjectRequest(name, code, larkWebhookUrl, opsContext),
                cancellationToken),
        });

    [McpServerTool(Name = "project_update", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("更新项目；larkWebhookUrl 传掩码或空值保持原值。")]
    public Task<string> UpdateProjectAsync(
        string id,
        string name,
        string? code = null,
        string? larkWebhookUrl = null,
        string? opsContext = null,
        bool? isArchived = null,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () =>
        {
            await management.UpdateProjectAsync(
                ToolResponse.ParseId(id),
                new UpdateProjectRequest(name, code, larkWebhookUrl, opsContext, isArchived),
                cancellationToken);
            return null;
        });

    [McpServerTool(Name = "project_archive", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("归档或取消归档项目（v1 无硬删）。")]
    public Task<string> ArchiveProjectAsync(
        string id,
        bool archived = true,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () =>
        {
            await management.SetProjectArchivedAsync(ToolResponse.ParseId(id), archived, cancellationToken);
            return null;
        });

    // ---- 环境 ----

    [McpServerTool(Name = "environment_create", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("在项目下创建环境。")]
    public Task<string> CreateEnvironmentAsync(
        string projectId,
        string name,
        bool isDefault = false,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => new
        {
            id = await management.CreateEnvironmentAsync(
                new CreateEnvironmentRequest(ToolResponse.ParseId(projectId, "projectId"), name, isDefault),
                cancellationToken),
        });

    [McpServerTool(Name = "environment_update", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("更新环境名称 / 默认标记 / 归档标记。")]
    public Task<string> UpdateEnvironmentAsync(
        string id,
        string name,
        bool? isDefault = null,
        bool? isArchived = null,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () =>
        {
            await management.UpdateEnvironmentAsync(
                ToolResponse.ParseId(id),
                new UpdateEnvironmentRequest(name, isDefault, isArchived),
                cancellationToken);
            return null;
        });

    [McpServerTool(Name = "environment_archive", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("归档或取消归档环境。")]
    public Task<string> ArchiveEnvironmentAsync(
        string id,
        bool archived = true,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () =>
        {
            await management.SetEnvironmentArchivedAsync(ToolResponse.ParseId(id), archived, cancellationToken);
            return null;
        });

    // ---- MCP server ----

    [McpServerTool(Name = "mcp_server_create", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("登记 MCP server。transport 取 Stdio 或 Http；stdio 必填 command，Http 必填 url。")]
    public Task<string> CreateMcpServerAsync(
        string projectId,
        string name,
        string transport = "Stdio",
        string? environmentId = null,
        string? command = null,
        string? argumentsJson = null,
        string? url = null,
        bool isEnabled = true,
        int timeoutSeconds = 60,
        int maxResultBytes = 8192,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => new
        {
            id = await management.CreateMcpServerAsync(
                new CreateMcpServerRequest(
                    ToolResponse.ParseId(projectId, "projectId"),
                    name,
                    ToolResponse.ParseEnum(transport, McpTransport.Stdio),
                    ToolResponse.ParseOptionalId(environmentId, "environmentId"),
                    command,
                    argumentsJson,
                    url,
                    isEnabled,
                    timeoutSeconds,
                    maxResultBytes),
                cancellationToken),
        });

    [McpServerTool(Name = "mcp_server_update", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("更新 MCP server；command / argumentsJson / url 传掩码或空值保持原值。")]
    public Task<string> UpdateMcpServerAsync(
        string id,
        string name,
        string transport,
        string? environmentId = null,
        string? command = null,
        string? argumentsJson = null,
        string? url = null,
        bool? isEnabled = null,
        int? timeoutSeconds = null,
        int? maxResultBytes = null,
        bool? isArchived = null,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () =>
        {
            await management.UpdateMcpServerAsync(
                ToolResponse.ParseId(id),
                new UpdateMcpServerRequest(
                    name,
                    ToolResponse.ParseEnum(transport, McpTransport.Stdio),
                    ToolResponse.ParseOptionalId(environmentId, "environmentId"),
                    command,
                    argumentsJson,
                    url,
                    isEnabled,
                    timeoutSeconds,
                    maxResultBytes,
                    isArchived),
                cancellationToken);
            return null;
        });

    [McpServerTool(Name = "mcp_server_archive", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("归档或取消归档 MCP server。")]
    public Task<string> ArchiveMcpServerAsync(
        string id,
        bool archived = true,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () =>
        {
            await management.SetMcpServerArchivedAsync(ToolResponse.ParseId(id), archived, cancellationToken);
            return null;
        });

    // ---- 手册 ----

    [McpServerTool(Name = "runbook_create", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("创建手册；kind 取 General 或 Specialized。")]
    public Task<string> CreateRunbookAsync(
        string projectId,
        string title,
        string? body = null,
        string kind = "Specialized",
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => new
        {
            id = await management.CreateRunbookAsync(
                new CreateRunbookRequest(
                    ToolResponse.ParseId(projectId, "projectId"),
                    title,
                    body,
                    ToolResponse.ParseEnum(kind, RunbookKind.Specialized)),
                cancellationToken),
        });

    [McpServerTool(Name = "runbook_update", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("更新手册标题 / Kind / 正文 / 归档标记。")]
    public Task<string> UpdateRunbookAsync(
        string id,
        string title,
        string kind,
        string body,
        bool? isArchived = null,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () =>
        {
            await management.UpdateRunbookAsync(
                ToolResponse.ParseId(id),
                new UpdateRunbookRequest(title, ToolResponse.ParseEnum(kind, RunbookKind.Specialized), body, isArchived),
                cancellationToken);
            return null;
        });

    [McpServerTool(Name = "runbook_archive", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("归档或取消归档手册。")]
    public Task<string> ArchiveRunbookAsync(
        string id,
        bool archived = true,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () =>
        {
            await management.SetRunbookArchivedAsync(ToolResponse.ParseId(id), archived, cancellationToken);
            return null;
        });

    // ---- 检查单 ----

    [McpServerTool(Name = "checklist_create", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("创建检查单；frequency 取 Manual / Hourly / Daily。")]
    public Task<string> CreateChecklistAsync(
        string projectId,
        string name,
        string? description = null,
        string frequency = "Manual",
        bool isEnabled = true,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => new
        {
            id = await management.CreateChecklistAsync(
                new CreateChecklistRequest(
                    ToolResponse.ParseId(projectId, "projectId"),
                    name,
                    description,
                    ToolResponse.ParseEnum(frequency, ChecklistFrequency.Manual),
                    isEnabled),
                cancellationToken),
        });

    [McpServerTool(Name = "checklist_update", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("更新检查单名称 / 说明 / 频率 / 启用 / 归档标记。")]
    public Task<string> UpdateChecklistAsync(
        string id,
        string name,
        string frequency,
        string? description = null,
        bool isEnabled = true,
        bool? isArchived = null,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () =>
        {
            await management.UpdateChecklistAsync(
                ToolResponse.ParseId(id),
                new UpdateChecklistRequest(
                    name,
                    description,
                    ToolResponse.ParseEnum(frequency, ChecklistFrequency.Manual),
                    isEnabled,
                    isArchived),
                cancellationToken);
            return null;
        });

    [McpServerTool(Name = "checklist_archive", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("归档或取消归档检查单。")]
    public Task<string> ArchiveChecklistAsync(
        string id,
        bool archived = true,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () =>
        {
            await management.SetChecklistArchivedAsync(ToolResponse.ParseId(id), archived, cancellationToken);
            return null;
        });

    // ---- 检查项 ----

    [McpServerTool(Name = "checklist_item_create", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("创建检查项：title + 自然语言 body（查什么 / 什么算健康 / 异常先看哪）+ 可选 severity（Info,Warning,Critical）。")]
    public Task<string> CreateChecklistItemAsync(
        string checklistId,
        string title,
        string body,
        string? severity = null,
        int? sortOrder = null,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () => new
        {
            id = await management.CreateChecklistItemAsync(
                new CreateChecklistItemRequest(
                    ToolResponse.ParseId(checklistId, "checklistId"),
                    BuildItemInput(title, body, severity),
                    sortOrder),
                cancellationToken),
        });

    [McpServerTool(Name = "checklist_item_update", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("更新检查项：title + 自然语言 body + 可选 severity。")]
    public Task<string> UpdateChecklistItemAsync(
        string id,
        string title,
        string body,
        string? severity = null,
        int? sortOrder = null,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () =>
        {
            await management.UpdateChecklistItemAsync(
                ToolResponse.ParseId(id),
                new UpdateChecklistItemRequest(
                    BuildItemInput(title, body, severity),
                    sortOrder),
                cancellationToken);
            return null;
        });

    [McpServerTool(Name = "checklist_item_archive", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("归档或取消归档检查项。")]
    public Task<string> ArchiveChecklistItemAsync(
        string id,
        bool archived = true,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () =>
        {
            await management.SetChecklistItemArchivedAsync(ToolResponse.ParseId(id), archived, cancellationToken);
            return null;
        });

    // ---- 检查单 ↔ 手册引用 ----

    [McpServerTool(Name = "checklist_runbook_add", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("给检查单引用一份同项目的未归档专用手册。")]
    public Task<string> AddChecklistRunbookAsync(
        string checklistId,
        string runbookId,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () =>
        {
            await management.AddChecklistRunbookRefAsync(
                ToolResponse.ParseId(checklistId, "checklistId"),
                ToolResponse.ParseId(runbookId, "runbookId"),
                cancellationToken);
            return null;
        });

    [McpServerTool(Name = "checklist_runbook_remove", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("移除检查单对某份手册的引用。")]
    public Task<string> RemoveChecklistRunbookAsync(
        string checklistId,
        string runbookId,
        CancellationToken cancellationToken = default)
        => ToolResponse.RunAsync(async () =>
        {
            await management.RemoveChecklistRunbookRefAsync(
                ToolResponse.ParseId(checklistId, "checklistId"),
                ToolResponse.ParseId(runbookId, "runbookId"),
                cancellationToken);
            return null;
        });

    private static ChecklistItemInput BuildItemInput(
        string title,
        string body,
        string? severity)
        => new(
            title,
            body,
            ToolResponse.ParseOptionalEnum<Severity>(severity, "severity"));
}
