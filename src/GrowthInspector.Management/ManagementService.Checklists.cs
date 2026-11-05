using System.Text.Json;
using GrowthInspector.Core.Models;
using GrowthInspector.Data;
using Microsoft.EntityFrameworkCore;

namespace GrowthInspector.Management;

public sealed partial class ManagementService
{
    // ---- 检查单 ----

    public async Task<PagedResult<ChecklistView>> ListChecklistsAsync(
        Guid? projectId = null,
        bool includeArchived = false,
        int limit = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Checklists.AsNoTracking()
            .Where(x => projectId == null || x.ProjectId == projectId)
            .Where(x => includeArchived || !x.IsArchived)
            .OrderByDescending(x => x.UpdatedAt)
            .Select(x => new
            {
                x.Id,
                x.ProjectId,
                ProjectName = x.Project!.Name,
                x.Name,
                x.Description,
                x.Frequency,
                x.IsEnabled,
                x.IsArchived,
                x.CreatedAt,
                x.UpdatedAt,
                ItemCount = x.Items.Count,
                RunbookRefCount = x.RunbookRefs.Count,
            });

        var page = await ManagementRules.PageAsync(query, limit, cursor, cancellationToken);
        var lastRuns = await LoadLastRunTimesAsync(db, page.Items.Select(x => x.Id).ToList(), cancellationToken);

        return new PagedResult<ChecklistView>(
            page.Items.Select(x => new ChecklistView(
                x.Id,
                x.ProjectId,
                x.ProjectName,
                x.Name,
                x.Description,
                x.Frequency,
                x.IsEnabled,
                x.IsArchived,
                x.ItemCount,
                x.RunbookRefCount,
                lastRuns.GetValueOrDefault(x.Id),
                x.CreatedAt,
                x.UpdatedAt)).ToList(),
            page.NextCursor);
    }

    public async Task<ChecklistView?> GetChecklistAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var checklist = await db.Checklists.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id,
                x.ProjectId,
                ProjectName = x.Project!.Name,
                x.Name,
                x.Description,
                x.Frequency,
                x.IsEnabled,
                x.IsArchived,
                x.CreatedAt,
                x.UpdatedAt,
                ItemCount = x.Items.Count,
                RunbookRefCount = x.RunbookRefs.Count,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (checklist is null)
        {
            return null;
        }

        var lastRunAt = await db.ChecklistRuns.AsNoTracking()
            .Where(x => x.ChecklistId == id)
            .OrderByDescending(x => x.StartedAt)
            .Select(x => (DateTimeOffset?)x.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return new ChecklistView(
            checklist.Id,
            checklist.ProjectId,
            checklist.ProjectName,
            checklist.Name,
            checklist.Description,
            checklist.Frequency,
            checklist.IsEnabled,
            checklist.IsArchived,
            checklist.ItemCount,
            checklist.RunbookRefCount,
            lastRunAt,
            checklist.CreatedAt,
            checklist.UpdatedAt);
    }

    public async Task<Guid> CreateChecklistAsync(CreateChecklistRequest request, CancellationToken cancellationToken = default)
    {
        var name = ManagementRules.RequiredText(request.Name, "检查单名称", 200);
        ValidateFrequency(request.Frequency);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Projects.AnyAsync(x => x.Id == request.ProjectId, cancellationToken))
        {
            throw new ManagementException("项目不存在。");
        }

        await EnsureChecklistNameAvailableAsync(db, request.ProjectId, name, null, cancellationToken);

        var checklist = new Checklist
        {
            ProjectId = request.ProjectId,
            Name = name,
            Description = ManagementRules.OptionalText(request.Description, 2000),
            Frequency = request.Frequency,
            IsEnabled = request.IsEnabled,
        };

        db.Checklists.Add(checklist);
        await db.SaveChangesAsync(cancellationToken);
        return checklist.Id;
    }

    public async Task UpdateChecklistAsync(Guid id, UpdateChecklistRequest request, CancellationToken cancellationToken = default)
    {
        var name = ManagementRules.RequiredText(request.Name, "检查单名称", 200);
        ValidateFrequency(request.Frequency);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var checklist = await db.Checklists.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ManagementException("检查单不存在。");

        await EnsureChecklistNameAvailableAsync(db, checklist.ProjectId, name, id, cancellationToken);

        checklist.Name = name;
        checklist.Description = ManagementRules.OptionalText(request.Description, 2000);
        checklist.Frequency = request.Frequency;
        checklist.IsEnabled = request.IsEnabled;
        checklist.UpdatedAt = DateTimeOffset.UtcNow;
        if (request.IsArchived is { } archived)
        {
            checklist.IsArchived = archived;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetChecklistArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var checklist = await db.Checklists.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ManagementException("检查单不存在。");

        checklist.IsArchived = archived;
        checklist.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- 检查项 ----

    public async Task<PagedResult<ChecklistItemView>> ListChecklistItemsAsync(
        Guid checklistId,
        bool includeArchived = false,
        int limit = 200,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Checklists.AnyAsync(x => x.Id == checklistId, cancellationToken))
        {
            throw new ManagementException("检查单不存在。");
        }

        var query = db.ChecklistItems.AsNoTracking()
            .Where(x => x.ChecklistId == checklistId)
            .Where(x => includeArchived || !x.IsArchived)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.CreatedAt)
            .Select(x => new ChecklistItemView(
                x.Id,
                x.ChecklistId,
                x.Title,
                x.Body,
                x.SortOrder,
                x.Severity,
                x.IsArchived,
                x.CreatedAt,
                x.UpdatedAt));

        return await ManagementRules.PageAsync(query, limit, cursor, cancellationToken);
    }

    public async Task<ChecklistItemView?> GetChecklistItemAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ChecklistItems.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new ChecklistItemView(
                x.Id,
                x.ChecklistId,
                x.Title,
                x.Body,
                x.SortOrder,
                x.Severity,
                x.IsArchived,
                x.CreatedAt,
                x.UpdatedAt))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<Guid> CreateChecklistItemAsync(
        CreateChecklistItemRequest request,
        CancellationToken cancellationToken = default)
    {
        var input = NormalizeItemInput(request.Item);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Checklists.AnyAsync(x => x.Id == request.ChecklistId, cancellationToken))
        {
            throw new ManagementException("检查单不存在。");
        }

        var sortOrder = request.SortOrder ?? await NextSortOrderAsync(db, request.ChecklistId, cancellationToken);
        var item = new ChecklistItem
        {
            ChecklistId = request.ChecklistId,
            Title = input.Title,
            Body = input.Body,
            SortOrder = sortOrder,
            Severity = input.Severity,
        };

        db.ChecklistItems.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        return item.Id;
    }

    public async Task UpdateChecklistItemAsync(
        Guid id,
        UpdateChecklistItemRequest request,
        CancellationToken cancellationToken = default)
    {
        var input = NormalizeItemInput(request.Item);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.ChecklistItems.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ManagementException("检查项不存在。");

        item.Title = input.Title;
        item.Body = input.Body;
        item.Severity = input.Severity;
        if (request.SortOrder is { } sortOrder)
        {
            item.SortOrder = sortOrder;
        }

        item.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetChecklistItemArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.ChecklistItems.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ManagementException("检查项不存在。");

        item.IsArchived = archived;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- 检查单 ↔ 手册引用 ----

    public async Task<PagedResult<ChecklistRunbookRefView>> ListChecklistRunbookRefsAsync(
        Guid checklistId,
        int limit = 200,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.ChecklistRunbookRefs.AsNoTracking()
            .Where(x => x.ChecklistId == checklistId)
            .OrderBy(x => x.Runbook!.Title)
            .Select(x => new ChecklistRunbookRefView(
                x.ChecklistId,
                x.Checklist!.Name,
                x.RunbookId,
                x.Runbook!.Title,
                x.Runbook.Kind,
                x.Runbook.IsArchived));

        return await ManagementRules.PageAsync(query, limit, cursor, cancellationToken);
    }

    public async Task<IReadOnlyList<RunbookSummaryView>> ListAssignableRunbooksAsync(
        Guid checklistId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var checklist = await db.Checklists.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == checklistId, cancellationToken)
            ?? throw new ManagementException("检查单不存在。");

        var runbooks = await db.Runbooks.AsNoTracking()
            .Where(x => x.ProjectId == checklist.ProjectId
                && x.Kind == RunbookKind.Specialized
                && !x.IsArchived)
            .OrderBy(x => x.Title)
            .ToListAsync(cancellationToken);

        var referenceCounts = await CountRunbookReferencesAsync(
            db,
            runbooks.Select(x => x.Id).ToList(),
            cancellationToken);

        return runbooks
            .Select(x => new RunbookSummaryView(
                x.Id,
                x.ProjectId,
                string.Empty,
                x.Title,
                x.Kind,
                x.IsArchived,
                referenceCounts.GetValueOrDefault(x.Id),
                x.CreatedAt,
                x.UpdatedAt))
            .ToList();
    }

    public async Task AddChecklistRunbookRefAsync(
        Guid checklistId,
        Guid runbookId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var checklist = await db.Checklists.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == checklistId, cancellationToken)
            ?? throw new ManagementException("检查单不存在。");

        var runbook = await db.Runbooks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == runbookId, cancellationToken)
            ?? throw new ManagementException("手册不存在。");

        if (runbook.ProjectId != checklist.ProjectId)
        {
            throw new ManagementException("只能引用同一 Project 下的专用手册。");
        }

        if (runbook.IsArchived)
        {
            throw new ManagementException("已归档的手册不能被检查单引用。");
        }

        if (runbook.Kind != RunbookKind.Specialized)
        {
            throw new ManagementException("通用手册会自动注入，不需要被检查单引用。");
        }

        var exists = await db.ChecklistRunbookRefs
            .AnyAsync(x => x.ChecklistId == checklistId && x.RunbookId == runbookId, cancellationToken);
        if (exists)
        {
            return;
        }

        db.ChecklistRunbookRefs.Add(new ChecklistRunbookRef
        {
            ChecklistId = checklistId,
            RunbookId = runbookId,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveChecklistRunbookRefAsync(
        Guid checklistId,
        Guid runbookId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var reference = await db.ChecklistRunbookRefs
            .SingleOrDefaultAsync(x => x.ChecklistId == checklistId && x.RunbookId == runbookId, cancellationToken);

        if (reference is null)
        {
            return;
        }

        db.ChecklistRunbookRefs.Remove(reference);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<Dictionary<Guid, DateTimeOffset>> LoadLastRunTimesAsync(
        GrowthInspectorDbContext db,
        IReadOnlyCollection<Guid> checklistIds,
        CancellationToken cancellationToken)
    {
        if (checklistIds.Count == 0)
        {
            return [];
        }

        return await db.ChecklistRuns.AsNoTracking()
            .Where(x => checklistIds.Contains(x.ChecklistId))
            .GroupBy(x => x.ChecklistId)
            .Select(group => new { ChecklistId = group.Key, LastRunAt = group.Max(x => x.StartedAt) })
            .ToDictionaryAsync(x => x.ChecklistId, x => x.LastRunAt, cancellationToken);
    }

    private static async Task<int> NextSortOrderAsync(
        GrowthInspectorDbContext db,
        Guid checklistId,
        CancellationToken cancellationToken)
    {
        var max = await db.ChecklistItems
            .Where(x => x.ChecklistId == checklistId)
            .Select(x => (int?)x.SortOrder)
            .MaxAsync(cancellationToken);

        return (max ?? -1) + 1;
    }

    private static void ValidateFrequency(ChecklistFrequency frequency)
    {
        if (!Enum.IsDefined(frequency))
        {
            throw new ManagementException("检查频率无效。");
        }
    }

    private static async Task EnsureChecklistNameAvailableAsync(
        GrowthInspectorDbContext db,
        Guid projectId,
        string name,
        Guid? excludeId,
        CancellationToken cancellationToken)
    {
        var exists = await db.Checklists.AnyAsync(
            x => x.ProjectId == projectId && x.Name == name && (excludeId == null || x.Id != excludeId),
            cancellationToken);

        if (exists)
        {
            throw new ManagementException($"同一项目下已存在检查单「{name}」。");
        }
    }

    private static ChecklistItemInput NormalizeItemInput(ChecklistItemInput raw)
    {
        var title = ManagementRules.RequiredText(raw.Title, "检查项名称", 300);
        var body = ManagementRules.RequiredText(raw.Body, "检查项正文", 4000);

        if (raw.Severity is { } severity && !Enum.IsDefined(severity))
        {
            throw new ManagementException("严重级别无效。");
        }

        return new ChecklistItemInput(title, body, raw.Severity);
    }
}
