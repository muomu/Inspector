using System.Text;
using System.Text.RegularExpressions;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Data;
using Microsoft.EntityFrameworkCore;

namespace GrowthInspector.Management;

public sealed partial class ManagementService
{
    // ---- 运维手册 ----

    public async Task<PagedResult<RunbookSummaryView>> ListRunbooksAsync(
        Guid? projectId = null,
        RunbookKind? kind = null,
        bool includeArchived = false,
        int limit = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Runbooks.AsNoTracking()
            .Where(x => projectId == null || x.ProjectId == projectId)
            .Where(x => kind == null || x.Kind == kind)
            .Where(x => includeArchived || !x.IsArchived)
            .OrderByDescending(x => x.UpdatedAt)
            .Select(x => new
            {
                x.Id,
                x.ProjectId,
                ProjectName = x.Project!.Name,
                x.Title,
                x.Kind,
                x.IsArchived,
                x.CreatedAt,
                x.UpdatedAt,
            });

        var page = await ManagementRules.PageAsync(query, limit, cursor, cancellationToken);
        var ids = page.Items.Select(x => x.Id).ToList();
        var referenceCounts = await CountRunbookReferencesAsync(db, ids, cancellationToken);

        return new PagedResult<RunbookSummaryView>(
            page.Items.Select(x => new RunbookSummaryView(
                x.Id,
                x.ProjectId,
                x.ProjectName,
                x.Title,
                x.Kind,
                x.IsArchived,
                referenceCounts.GetValueOrDefault(x.Id),
                x.CreatedAt,
                x.UpdatedAt)).ToList(),
            page.NextCursor);
    }

    public async Task<RunbookView?> GetRunbookAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var runbook = await db.Runbooks.AsNoTracking()
            .Include(x => x.Project)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (runbook is null)
        {
            return null;
        }

        var referenceCount = await db.ChecklistRunbookRefs.CountAsync(x => x.RunbookId == id, cancellationToken);
        return new RunbookView(
            runbook.Id,
            runbook.ProjectId,
            runbook.Project?.Name ?? string.Empty,
            runbook.Title,
            runbook.Kind,
            runbook.Body,
            runbook.IsArchived,
            referenceCount,
            runbook.CreatedAt,
            runbook.UpdatedAt);
    }

    public async Task<Guid> CreateRunbookAsync(CreateRunbookRequest request, CancellationToken cancellationToken = default)
    {
        var title = ManagementRules.RequiredText(request.Title, "手册标题", 200);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Projects.AnyAsync(x => x.Id == request.ProjectId, cancellationToken))
        {
            throw new ManagementException("项目不存在。");
        }

        await EnsureRunbookTitleAvailableAsync(db, request.ProjectId, title, null, cancellationToken);

        var runbook = new Runbook
        {
            ProjectId = request.ProjectId,
            Title = title,
            Kind = request.Kind,
            Body = string.IsNullOrWhiteSpace(request.Body) ? $"# {title}\n" : request.Body,
        };

        db.Runbooks.Add(runbook);
        await db.SaveChangesAsync(cancellationToken);
        return runbook.Id;
    }

    public async Task UpdateRunbookAsync(Guid id, UpdateRunbookRequest request, CancellationToken cancellationToken = default)
    {
        var title = ManagementRules.RequiredText(request.Title, "手册标题", 200);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var runbook = await db.Runbooks.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ManagementException("手册不存在。");

        await EnsureRunbookTitleAvailableAsync(db, runbook.ProjectId, title, id, cancellationToken);

        runbook.Title = title;
        runbook.Kind = request.Kind;
        runbook.Body = request.Body ?? string.Empty;
        runbook.UpdatedAt = DateTimeOffset.UtcNow;
        if (request.IsArchived is { } archived)
        {
            runbook.IsArchived = archived;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetRunbookArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var runbook = await db.Runbooks.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ManagementException("手册不存在。");

        runbook.IsArchived = archived;
        runbook.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- IRunbookWriteService：反思 agent 的进程内写入口 ----

    public async Task<RunbookSectionUpdateResult> UpdateSectionAsync(
        Guid runbookId,
        string? sectionHeading,
        string newContent,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var runbook = await db.Runbooks.SingleOrDefaultAsync(x => x.Id == runbookId, cancellationToken);
        if (runbook is null)
        {
            return new RunbookSectionUpdateResult(false, "手册不存在。");
        }

        if (runbook.IsArchived)
        {
            return new RunbookSectionUpdateResult(false, "手册已归档，不能改写。");
        }

        runbook.Body = UpsertMarkdownSection(runbook.Body, sectionHeading, newContent ?? string.Empty);
        runbook.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return new RunbookSectionUpdateResult(true);
    }

    private static async Task<Dictionary<Guid, int>> CountRunbookReferencesAsync(
        GrowthInspectorDbContext db,
        IReadOnlyCollection<Guid> runbookIds,
        CancellationToken cancellationToken)
    {
        if (runbookIds.Count == 0)
        {
            return [];
        }

        return await db.ChecklistRunbookRefs.AsNoTracking()
            .Where(x => runbookIds.Contains(x.RunbookId))
            .GroupBy(x => x.RunbookId)
            .Select(group => new { RunbookId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.RunbookId, x => x.Count, cancellationToken);
    }

    private static async Task EnsureRunbookTitleAvailableAsync(
        GrowthInspectorDbContext db,
        Guid projectId,
        string title,
        Guid? excludeId,
        CancellationToken cancellationToken)
    {
        var exists = await db.Runbooks.AnyAsync(
            x => x.ProjectId == projectId && x.Title == title && (excludeId == null || x.Id != excludeId),
            cancellationToken);

        if (exists)
        {
            throw new ManagementException($"同一项目下已存在手册「{title}」。");
        }
    }

    /// <summary>
    /// 定位并替换 Markdown 中指定标题的章节；标题不存在时追加到文末，标题为空时直接追加到文末。
    /// 章节范围到下一个同级或更高级标题为止，正文原样保留其余章节。
    /// </summary>
    internal static string UpsertMarkdownSection(string? body, string? sectionHeading, string newContent)
    {
        var normalized = (body ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.EndsWith('\n'))
        {
            normalized += "\n";
        }

        var content = newContent.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (string.IsNullOrWhiteSpace(sectionHeading))
        {
            return string.IsNullOrWhiteSpace(normalized.Trim())
                ? content + "\n"
                : normalized.TrimEnd() + "\n\n" + content + "\n";
        }

        var heading = sectionHeading.Trim();
        var lines = normalized.Split('\n').ToList();
        var headingPattern = new Regex(
            $"^(?<level>#{{1,6}})\\s+{Regex.Escape(heading)}\\s*$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        var start = -1;
        var level = 0;
        for (var index = 0; index < lines.Count; index++)
        {
            var match = headingPattern.Match(lines[index]);
            if (!match.Success)
            {
                continue;
            }

            start = index;
            level = match.Groups["level"].Value.Length;
            break;
        }

        if (start < 0)
        {
            var trimmed = normalized.TrimEnd();
            var prefix = trimmed.Length == 0 ? string.Empty : trimmed + "\n\n";
            return prefix + $"## {heading}\n\n" + (content.Length == 0 ? string.Empty : content + "\n");
        }

        var end = lines.Count;
        var nextHeadingPattern = new Regex($"^#{{1,{level}}}\\s+", RegexOptions.CultureInvariant);
        for (var index = start + 1; index < lines.Count; index++)
        {
            if (nextHeadingPattern.IsMatch(lines[index]))
            {
                end = index;
                break;
            }
        }

        var builder = new StringBuilder();
        for (var index = 0; index <= start; index++)
        {
            builder.Append(lines[index]).Append('\n');
        }

        if (content.Length > 0)
        {
            builder.Append('\n').Append(content).Append('\n');
            if (end < lines.Count)
            {
                builder.Append('\n');
            }
        }

        for (var index = end; index < lines.Count; index++)
        {
            builder.Append(lines[index]).Append('\n');
        }

        return builder.ToString();
    }
}
