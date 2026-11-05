using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;

namespace GrowthInspector.Management;

/// <summary>
/// 管理 service：页面、管理 MCP 与反思 agent 共用的唯一写入入口。
/// 读方法默认过滤归档；写方法负责名称唯一、同 Project 引用校验与凭据掩码语义。
/// 校验 / 引用失败抛 <see cref="ManagementException"/>。
/// </summary>
public interface IManagementService : IRunbookWriteService
{
    // ---- 项目 ----

    Task<PagedResult<ProjectView>> ListProjectsAsync(
        bool includeArchived = false, int limit = 50, string? cursor = null, CancellationToken cancellationToken = default);

    Task<ProjectView?> GetProjectAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Guid> CreateProjectAsync(CreateProjectRequest request, CancellationToken cancellationToken = default);

    Task UpdateProjectAsync(Guid id, UpdateProjectRequest request, CancellationToken cancellationToken = default);

    Task SetProjectArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken = default);

    // ---- 环境 ----

    Task<PagedResult<EnvironmentView>> ListEnvironmentsAsync(
        Guid? projectId = null, bool includeArchived = false, int limit = 50, string? cursor = null,
        CancellationToken cancellationToken = default);

    Task<EnvironmentView?> GetEnvironmentAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Guid> CreateEnvironmentAsync(CreateEnvironmentRequest request, CancellationToken cancellationToken = default);

    Task UpdateEnvironmentAsync(Guid id, UpdateEnvironmentRequest request, CancellationToken cancellationToken = default);

    Task SetEnvironmentArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken = default);

    // ---- MCP server ----

    Task<PagedResult<McpServerView>> ListMcpServersAsync(
        Guid? projectId = null, bool includeArchived = false, int limit = 50, string? cursor = null,
        CancellationToken cancellationToken = default);

    Task<McpServerView?> GetMcpServerAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Guid> CreateMcpServerAsync(CreateMcpServerRequest request, CancellationToken cancellationToken = default);

    Task UpdateMcpServerAsync(Guid id, UpdateMcpServerRequest request, CancellationToken cancellationToken = default);

    Task SetMcpServerArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken = default);

    // ---- 运维手册 ----

    Task<PagedResult<RunbookSummaryView>> ListRunbooksAsync(
        Guid? projectId = null, RunbookKind? kind = null, bool includeArchived = false,
        int limit = 50, string? cursor = null, CancellationToken cancellationToken = default);

    Task<RunbookView?> GetRunbookAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Guid> CreateRunbookAsync(CreateRunbookRequest request, CancellationToken cancellationToken = default);

    Task UpdateRunbookAsync(Guid id, UpdateRunbookRequest request, CancellationToken cancellationToken = default);

    Task SetRunbookArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken = default);

    // ---- 检查单 ----

    Task<PagedResult<ChecklistView>> ListChecklistsAsync(
        Guid? projectId = null, bool includeArchived = false, int limit = 50, string? cursor = null,
        CancellationToken cancellationToken = default);

    Task<ChecklistView?> GetChecklistAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Guid> CreateChecklistAsync(CreateChecklistRequest request, CancellationToken cancellationToken = default);

    Task UpdateChecklistAsync(Guid id, UpdateChecklistRequest request, CancellationToken cancellationToken = default);

    Task SetChecklistArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken = default);

    // ---- 检查项 ----

    Task<PagedResult<ChecklistItemView>> ListChecklistItemsAsync(
        Guid checklistId, bool includeArchived = false, int limit = 200, string? cursor = null,
        CancellationToken cancellationToken = default);

    Task<ChecklistItemView?> GetChecklistItemAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Guid> CreateChecklistItemAsync(CreateChecklistItemRequest request, CancellationToken cancellationToken = default);

    Task UpdateChecklistItemAsync(Guid id, UpdateChecklistItemRequest request, CancellationToken cancellationToken = default);

    Task SetChecklistItemArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken = default);

    // ---- 检查单 ↔ 手册引用 ----

    Task<PagedResult<ChecklistRunbookRefView>> ListChecklistRunbookRefsAsync(
        Guid checklistId, int limit = 200, string? cursor = null, CancellationToken cancellationToken = default);

    /// <summary>可被检查单引用的手册：同 Project、未归档、专用。</summary>
    Task<IReadOnlyList<RunbookSummaryView>> ListAssignableRunbooksAsync(
        Guid checklistId, CancellationToken cancellationToken = default);

    Task AddChecklistRunbookRefAsync(Guid checklistId, Guid runbookId, CancellationToken cancellationToken = default);

    Task RemoveChecklistRunbookRefAsync(Guid checklistId, Guid runbookId, CancellationToken cancellationToken = default);

    // ---- 执行 / 结果 / 异常 / 摘要 / 工具调用（只读） ----

    Task<PagedResult<ChecklistRunView>> ListRunsAsync(
        Guid? checklistId = null, Guid? environmentId = null, RunStatus? status = null,
        int limit = 50, string? cursor = null, CancellationToken cancellationToken = default);

    Task<ChecklistRunView?> GetRunAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PagedResult<CheckResultView>> ListCheckResultsAsync(
        Guid runId, int limit = 200, string? cursor = null, CancellationToken cancellationToken = default);

    Task<CheckResultView?> GetCheckResultAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PagedResult<FindingView>> ListFindingsAsync(
        Guid? projectId = null, Guid? environmentId = null, FindingStatus? status = null,
        int limit = 50, string? cursor = null, CancellationToken cancellationToken = default);

    Task<FindingView?> GetFindingAsync(Guid id, CancellationToken cancellationToken = default);

    Task<AiSummaryView?> GetRunSummaryAsync(Guid runId, CancellationToken cancellationToken = default);

    Task<PagedResult<AiSummaryView>> ListDailyReportsAsync(
        DateOnly? reportDate = null, int limit = 31, string? cursor = null,
        CancellationToken cancellationToken = default);

    Task<PagedResult<ToolCallLogView>> ListToolCallsAsync(
        Guid runId, Guid? checklistItemId = null, int limit = 100, string? cursor = null,
        CancellationToken cancellationToken = default);

    // ---- 触发运行 ----

    /// <summary>把一次手动 / 管理触发写入后台队列；不阻塞调用方，不直接执行。</summary>
    Task<ChecklistRunEnqueuedResult> EnqueueChecklistRunAsync(
        Guid checklistId, Guid environmentId, ChecklistTrigger trigger = ChecklistTrigger.Manual,
        CancellationToken cancellationToken = default);

    // ---- 模型接入（LLM API key） ----

    /// <summary>读取全局 LLM 配置；key 只返回掩码，另带 appsettings 的出厂预填值。</summary>
    Task<LlmSettingsView> GetLlmSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>保存全局 LLM 配置（三字段必填，key 传掩码或空表示沿用已有 key）。</summary>
    Task SaveLlmSettingsAsync(SaveLlmSettingsRequest request, CancellationToken cancellationToken = default);

    /// <summary>只清除 API key，保留 BaseUrl 与模型。</summary>
    Task ClearLlmApiKeyAsync(CancellationToken cancellationToken = default);

    /// <summary>用当前表单值发一次最小 chat 请求试连；不改动已保存的配置。</summary>
    Task<LlmConnectionTestResult> TestLlmConnectionAsync(
        TestLlmConnectionRequest request, CancellationToken cancellationToken = default);
}
