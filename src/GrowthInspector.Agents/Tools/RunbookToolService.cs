using System.Text.Json;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Options;
using GrowthInspector.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Agents.Tools;

/// <summary>
/// 本地手册工具的实现：检索、渐进披露、列表与章节改写。
/// 所有方法都返回 JSON 字符串；业务失败返回 <c>{"ok":false,"error":...}</c>，不向 agent 抛异常。
/// </summary>
public sealed class RunbookToolService(
    IDbContextFactory<GrowthInspectorDbContext> dbContextFactory,
    RunbookSearchService searchService,
    IOptions<InspectionOptions> options,
    IRunbookWriteService? runbookWriteService,
    ILogger<RunbookToolService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>检索同 Project、未归档的专用手册，必须中文子串命中。</summary>
    public async Task<string> SearchAsync(Guid projectId, string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Error("query 不能为空。");
        }

        var limit = Math.Max(1, options.Value.RunbookSearchLimit);
        var hits = await searchService.SearchAsync(projectId, query, cancellationToken);

        return Serialize(new
        {
            ok = true,
            query,
            count = hits.Count,
            limit,
            results = hits.Select(hit => new
            {
                runbookId = hit.RunbookId.ToString(),
                title = hit.Title,
                snippet = hit.Snippet,
            }),
        });
    }

    /// <summary>读取手册全文；传 section 时只返回该章节（渐进披露）。</summary>
    public async Task<string> ReadAsync(
        Guid projectId,
        string runbookId,
        string? section,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(runbookId, out var id))
        {
            return Error("runbookId 不是合法的 GUID。");
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var runbook = await db.Runbooks
            .AsNoTracking()
            .Where(item => item.Id == id && item.ProjectId == projectId && !item.IsArchived)
            .Select(item => new { item.Id, item.Title, item.Kind, item.Body })
            .SingleOrDefaultAsync(cancellationToken);

        if (runbook is null)
        {
            return Error("手册不存在、不属于当前 Project 或已归档。");
        }

        if (string.IsNullOrWhiteSpace(section))
        {
            return Serialize(new
            {
                ok = true,
                runbookId = runbook.Id.ToString(),
                title = runbook.Title,
                kind = runbook.Kind.ToString(),
                section = (string?)null,
                content = runbook.Body,
            });
        }

        var found = RunbookMarkdown.FindSection(runbook.Body, section);
        if (found is null)
        {
            return Serialize(new
            {
                ok = false,
                runbookId = runbook.Id.ToString(),
                error = $"手册中不存在章节「{section.Trim()}」。",
                availableSections = RunbookMarkdown.ListHeadings(runbook.Body),
            });
        }

        return Serialize(new
        {
            ok = true,
            runbookId = runbook.Id.ToString(),
            title = runbook.Title,
            kind = runbook.Kind.ToString(),
            section = found.Heading,
            content = found.Content,
        });
    }

    /// <summary>列出同 Project 未归档手册的 Id / ProjectId / Kind / 标题。</summary>
    public async Task<string> ListAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var runbooks = await db.Runbooks
            .AsNoTracking()
            .Where(item => item.ProjectId == projectId && !item.IsArchived)
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Title)
            .Select(item => new
            {
                id = item.Id.ToString(),
                projectId = item.ProjectId.ToString(),
                kind = item.Kind.ToString(),
                title = item.Title,
            })
            .ToListAsync(cancellationToken);

        return Serialize(new { ok = true, count = runbooks.Count, runbooks });
    }

    /// <summary>直调管理 service 改写章节；失败返回 ok=false，不抛。</summary>
    public async Task<string> UpdateSectionAsync(
        Guid projectId,
        string runbookId,
        string? section,
        string content,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(runbookId, out var id))
        {
            return Error("runbookId 不是合法的 GUID。");
        }

        if (content is null)
        {
            return Error("content 不能为空。");
        }

        if (runbookWriteService is null)
        {
            return Error("手册写服务未注册（IRunbookWriteService），本次修改未执行。");
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var exists = await db.Runbooks
            .AsNoTracking()
            .AnyAsync(item => item.Id == id && item.ProjectId == projectId && !item.IsArchived, cancellationToken);
        if (!exists)
        {
            return Error("手册不存在、不属于当前 Project 或已归档，拒绝改写。");
        }

        try
        {
            var result = await runbookWriteService.UpdateSectionAsync(id, section, content, cancellationToken);
            if (!result.Ok)
            {
                return Error(result.Error ?? "手册改写失败。");
            }

            return Serialize(new
            {
                ok = true,
                runbookId = id.ToString(),
                section,
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "改写手册章节失败（runbookId={RunbookId}）。", id);
            return Error(ex.Message);
        }
    }

    private static string Error(string message) => Serialize(new { ok = false, error = message });

    private static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);
}
