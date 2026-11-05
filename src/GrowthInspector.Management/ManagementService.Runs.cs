using GrowthInspector.Core.Models;
using GrowthInspector.Data;
using Microsoft.EntityFrameworkCore;

namespace GrowthInspector.Management;

public sealed partial class ManagementService
{
    // ---- 执行 / 结果 / 异常 / 摘要 / 工具调用（只读） ----

    public async Task<PagedResult<ChecklistRunView>> ListRunsAsync(
        Guid? checklistId = null,
        Guid? environmentId = null,
        RunStatus? status = null,
        int limit = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.ChecklistRuns.AsNoTracking()
            .Where(x => checklistId == null || x.ChecklistId == checklistId)
            .Where(x => environmentId == null || x.EnvironmentId == environmentId)
            .Where(x => status == null || x.Status == status)
            .OrderByDescending(x => x.StartedAt)
            .Select(x => new ChecklistRunView(
                x.Id,
                x.ChecklistId,
                x.Checklist!.Name,
                x.EnvironmentId,
                x.Environment!.Name,
                x.Trigger,
                x.Status,
                x.StartedAt,
                x.CompletedAt,
                x.DurationMs,
                x.PromptTokens,
                x.CompletionTokens,
                x.Results.Count,
                x.ErrorMessage));

        return await ManagementRules.PageAsync(query, limit, cursor, cancellationToken);
    }

    public async Task<ChecklistRunView?> GetRunAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ChecklistRuns.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new ChecklistRunView(
                x.Id,
                x.ChecklistId,
                x.Checklist!.Name,
                x.EnvironmentId,
                x.Environment!.Name,
                x.Trigger,
                x.Status,
                x.StartedAt,
                x.CompletedAt,
                x.DurationMs,
                x.PromptTokens,
                x.CompletionTokens,
                x.Results.Count,
                x.ErrorMessage))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<PagedResult<CheckResultView>> ListCheckResultsAsync(
        Guid runId,
        int limit = 200,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.CheckResults.AsNoTracking()
            .Where(x => x.ChecklistRunId == runId)
            .OrderBy(x => x.CreatedAt)
            .Select(x => new CheckResultView(
                x.Id,
                x.ChecklistRunId,
                x.ChecklistItemId,
                x.ChecklistItem!.Title,
                x.Status,
                x.AgentVerdict,
                x.Narrative,
                x.Evidence,
                x.ObservationsJson,
                x.ToolCallCount,
                x.DurationMs,
                x.PromptTokens,
                x.CompletionTokens,
                x.ErrorMessage,
                x.CreatedAt));

        return await ManagementRules.PageAsync(query, limit, cursor, cancellationToken);
    }

    public async Task<CheckResultView?> GetCheckResultAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.CheckResults.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new CheckResultView(
                x.Id,
                x.ChecklistRunId,
                x.ChecklistItemId,
                x.ChecklistItem!.Title,
                x.Status,
                x.AgentVerdict,
                x.Narrative,
                x.Evidence,
                x.ObservationsJson,
                x.ToolCallCount,
                x.DurationMs,
                x.PromptTokens,
                x.CompletionTokens,
                x.ErrorMessage,
                x.CreatedAt))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<PagedResult<FindingView>> ListFindingsAsync(
        Guid? projectId = null,
        Guid? environmentId = null,
        FindingStatus? status = null,
        int limit = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Findings.AsNoTracking()
            .Where(x => projectId == null || x.ProjectId == projectId)
            .Where(x => environmentId == null || x.EnvironmentId == environmentId)
            .Where(x => status == null || x.Status == status)
            .OrderByDescending(x => x.LastSeenAt)
            .Select(x => new FindingView(
                x.Id,
                x.ProjectId,
                x.EnvironmentId,
                x.ChecklistId,
                x.ChecklistItemId,
                x.Title,
                x.Message,
                x.Severity,
                x.Status,
                x.CloseReason,
                x.Evidence,
                x.FirstSeenAt,
                x.LastSeenAt,
                x.ResolvedAt));

        return await ManagementRules.PageAsync(query, limit, cursor, cancellationToken);
    }

    public async Task<FindingView?> GetFindingAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Findings.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new FindingView(
                x.Id,
                x.ProjectId,
                x.EnvironmentId,
                x.ChecklistId,
                x.ChecklistItemId,
                x.Title,
                x.Message,
                x.Severity,
                x.Status,
                x.CloseReason,
                x.Evidence,
                x.FirstSeenAt,
                x.LastSeenAt,
                x.ResolvedAt))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<AiSummaryView?> GetRunSummaryAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.AiSummaries.AsNoTracking()
            .Where(x => x.ChecklistRunId == runId && x.Kind == AiSummaryKind.RunSummary)
            .Select(x => new AiSummaryView(
                x.Id,
                x.Kind,
                x.ChecklistRunId,
                x.ReportDate,
                x.Content,
                x.Model,
                x.FallbackUsed,
                x.PromptTokens,
                x.CompletionTokens,
                x.DurationMs,
                x.CreatedAt))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<PagedResult<AiSummaryView>> ListDailyReportsAsync(
        DateOnly? reportDate = null,
        int limit = 31,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.AiSummaries.AsNoTracking()
            .Where(x => x.Kind == AiSummaryKind.DailyReport)
            .Where(x => reportDate == null || x.ReportDate == reportDate)
            .OrderByDescending(x => x.ReportDate)
            .Select(x => new AiSummaryView(
                x.Id,
                x.Kind,
                x.ChecklistRunId,
                x.ReportDate,
                x.Content,
                x.Model,
                x.FallbackUsed,
                x.PromptTokens,
                x.CompletionTokens,
                x.DurationMs,
                x.CreatedAt));

        return await ManagementRules.PageAsync(query, limit, cursor, cancellationToken);
    }

    public async Task<PagedResult<ToolCallLogView>> ListToolCallsAsync(
        Guid runId,
        Guid? checklistItemId = null,
        int limit = 100,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.ToolCallLogs.AsNoTracking()
            .Where(x => x.ChecklistRunId == runId)
            .Where(x => checklistItemId == null || x.ChecklistItemId == checklistItemId)
            .OrderBy(x => x.CreatedAt)
            .Select(x => new ToolCallLogView(
                x.Id,
                x.ChecklistRunId,
                x.ChecklistItemId,
                x.AgentRole,
                x.McpServerId,
                x.ServerName,
                x.ToolName,
                x.Arguments,
                x.Result,
                x.Truncated,
                x.Succeeded,
                x.DurationMs,
                x.ErrorMessage,
                x.CreatedAt));

        return await ManagementRules.PageAsync(query, limit, cursor, cancellationToken);
    }

    // ---- 触发运行 ----

    public async Task<ChecklistRunEnqueuedResult> EnqueueChecklistRunAsync(
        Guid checklistId,
        Guid environmentId,
        ChecklistTrigger trigger = ChecklistTrigger.Manual,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var checklist = await db.Checklists.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == checklistId, cancellationToken)
            ?? throw new ManagementException("检查单不存在。");

        if (checklist.IsArchived)
        {
            throw new ManagementException("检查单已归档，不能触发运行。");
        }

        var environment = await db.Environments.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == environmentId, cancellationToken)
            ?? throw new ManagementException("环境不存在。");

        if (environment.ProjectId != checklist.ProjectId)
        {
            throw new ManagementException("检查单与环境不属于同一 Project，拒绝执行。");
        }

        if (environment.IsArchived)
        {
            throw new ManagementException("环境已归档，不能触发运行。");
        }

        await runQueue.EnqueueAsync(trigger, checklistId, environmentId, cancellationToken);
        return new ChecklistRunEnqueuedResult(checklistId, environmentId, trigger);
    }
}
