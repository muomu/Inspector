using GrowthInspector.Core.Models;
using GrowthInspector.Core.Options;
using GrowthInspector.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Agents.Tools;

/// <summary>手册检索命中：手册标识、标题与命中位置附近的片段。</summary>
public sealed record RunbookSearchHit(Guid RunbookId, string Title, string Snippet);

/// <summary>
/// 专用手册检索：只检索同 Project、未归档的专用手册，按中文子串命中。
/// 优先使用 SQLite FTS5 trigram 索引（首次使用时建表并按 Project 刷新索引）；
/// FTS5 不可用或关闭时退回对 Runbooks 表的 LIKE 查询，两条路径的匹配与排序行为一致。
/// </summary>
public sealed class RunbookSearchService
{
    private const string IndexTable = "RunbookSearchIndex";
    private const string CreateIndexSql =
        "CREATE VIRTUAL TABLE IF NOT EXISTS \"RunbookSearchIndex\" USING fts5(" +
        "runbook_id UNINDEXED, project_id UNINDEXED, title, body, tokenize='trigram');";

    private readonly IDbContextFactory<GrowthInspectorDbContext> dbContextFactory;
    private readonly IOptions<InspectionOptions> options;
    private readonly ILogger<RunbookSearchService> logger;
    private readonly SemaphoreSlim ftsGate = new(1, 1);
    private bool? ftsAvailable;

    public RunbookSearchService(
        IDbContextFactory<GrowthInspectorDbContext> dbContextFactory,
        IOptions<InspectionOptions> options,
        ILogger<RunbookSearchService> logger,
        bool enableFtsIndex = true)
    {
        this.dbContextFactory = dbContextFactory;
        this.options = options;
        this.logger = logger;
        FtsIndexEnabled = enableFtsIndex;
    }

    /// <summary>是否允许使用 FTS5 索引；false 时直接走 LIKE 兜底（单测用于覆盖两条路径）。</summary>
    public bool FtsIndexEnabled { get; }

    /// <summary>FTS5 索引当前是否可用；未探测时为空。</summary>
    public bool? FtsIndexAvailable => ftsAvailable;

    /// <summary>检索专用手册；空查询返回空列表。</summary>
    public async Task<IReadOnlyList<RunbookSearchHit>> SearchAsync(
        Guid projectId,
        string query,
        CancellationToken cancellationToken = default)
    {
        var trimmed = query?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return [];
        }

        var limit = Math.Max(1, options.Value.RunbookSearchLimit);

        if (!FtsIndexEnabled)
        {
            return await SearchWithLikeAsync(projectId, trimmed, limit, cancellationToken);
        }

        await ftsGate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            if (!await TryEnsureFtsIndexAsync(db, cancellationToken))
            {
                return await SearchWithLikeAsync(projectId, trimmed, limit, cancellationToken);
            }

            await SyncProjectIndexAsync(db, projectId, cancellationToken);

            var pattern = BuildLikePattern(trimmed);
            var sql = $$"""
                SELECT runbook_id AS "RunbookId", title AS "Title", body AS "Body"
                FROM "{{IndexTable}}"
                WHERE project_id = {0}
                  AND (title LIKE {1} ESCAPE '\' OR body LIKE {1} ESCAPE '\')
                ORDER BY title
                LIMIT {2}
                """;
            var rows = await db.Database
                .SqlQueryRaw<RunbookSearchRow>(sql, projectId.ToString(), pattern, limit)
                .ToListAsync(cancellationToken);

            return rows.Select(row => ToHit(row, trimmed)).ToList();
        }
        finally
        {
            ftsGate.Release();
        }
    }

    private async Task<IReadOnlyList<RunbookSearchHit>> SearchWithLikeAsync(
        Guid projectId,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var pattern = BuildLikePattern(query);

        var rows = await db.Runbooks
            .AsNoTracking()
            .Where(runbook => runbook.ProjectId == projectId
                && runbook.Kind == RunbookKind.Specialized
                && !runbook.IsArchived)
            .Where(runbook => EF.Functions.Like(runbook.Title, pattern, "\\")
                || EF.Functions.Like(runbook.Body, pattern, "\\"))
            .OrderBy(runbook => runbook.Title)
            .Take(limit)
            .Select(runbook => new RunbookSearchRow
            {
                RunbookId = runbook.Id.ToString(),
                Title = runbook.Title,
                Body = runbook.Body,
            })
            .ToListAsync(cancellationToken);

        return rows.Select(row => ToHit(row, query)).ToList();
    }

    private async Task<bool> TryEnsureFtsIndexAsync(
        GrowthInspectorDbContext db,
        CancellationToken cancellationToken)
    {
        if (ftsAvailable.HasValue)
        {
            return ftsAvailable.Value;
        }

        try
        {
            await db.Database.ExecuteSqlRawAsync(CreateIndexSql, cancellationToken);
            ftsAvailable = true;
            logger.LogDebug("卷册检索使用 SQLite FTS5 trigram 索引。");
        }
        catch (Exception ex)
        {
            ftsAvailable = false;
            logger.LogWarning(
                ex,
                "SQLite FTS5 trigram 索引不可用，专用手册检索退回 LIKE（行为保持一致）。");
        }

        return ftsAvailable.Value;
    }

    private static async Task SyncProjectIndexAsync(
        GrowthInspectorDbContext db,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var projectKey = projectId.ToString();

        // v1 手册量很小：每次检索按 Project 重建索引，避免为增删改维护增量同步。
        await db.Database.ExecuteSqlRawAsync(
            $"DELETE FROM \"{IndexTable}\" WHERE project_id = {{0}}",
            [projectKey],
            cancellationToken);

        var runbooks = await db.Runbooks
            .AsNoTracking()
            .Where(runbook => runbook.ProjectId == projectId
                && runbook.Kind == RunbookKind.Specialized
                && !runbook.IsArchived)
            .Select(runbook => new { runbook.Id, runbook.Title, runbook.Body })
            .ToListAsync(cancellationToken);

        foreach (var runbook in runbooks)
        {
            await db.Database.ExecuteSqlRawAsync(
                $"INSERT INTO \"{IndexTable}\" (runbook_id, project_id, title, body) VALUES ({{0}}, {{1}}, {{2}}, {{3}})",
                [runbook.Id.ToString(), projectKey, runbook.Title, runbook.Body],
                cancellationToken);
        }
    }

    private static RunbookSearchHit ToHit(RunbookSearchRow row, string query)
    {
        if (!Guid.TryParse(row.RunbookId, out var runbookId))
        {
            runbookId = Guid.Empty;
        }

        return new RunbookSearchHit(runbookId, row.Title, BuildSnippet(row.Body, query));
    }

    private static string BuildSnippet(string body, string query)
    {
        var normalized = body
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\n', ' ');

        const int radius = 40;
        const int length = 140;

        var index = normalized.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return normalized.Length <= length ? normalized : normalized[..length] + "…";
        }

        var start = Math.Max(0, index - radius);
        var take = Math.Min(length, normalized.Length - start);
        var snippet = normalized.Substring(start, take).Trim();
        if (start > 0)
        {
            snippet = "…" + snippet;
        }

        if (start + take < normalized.Length)
        {
            snippet += "…";
        }

        return snippet;
    }

    private static string BuildLikePattern(string query)
    {
        var escaped = query
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
        return "%" + escaped + "%";
    }

    private sealed class RunbookSearchRow
    {
        public string RunbookId { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Body { get; set; } = string.Empty;
    }
}
