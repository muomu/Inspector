using GrowthInspector.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace GrowthInspector.Web.Services;

/// <summary>顶栏「项目 / 环境」上下文中可选项。</summary>
public sealed record WorkspaceOption(
    Guid ProjectId,
    string ProjectName,
    string? ProjectCode,
    Guid EnvironmentId,
    string EnvironmentName,
    bool IsDefaultEnvironment)
{
    /// <summary>URL 中的稳定键（项目 / 环境各取 GUID，改名不影响链接）。</summary>
    public string Key => $"{ProjectId:N}:{EnvironmentId:N}";

    /// <summary>展示文案：项目 · 环境。</summary>
    public string Display => $"{ProjectName} · {EnvironmentName}";
}

/// <summary>一次页面请求解析出的上下文：全部可选项 + 当前选中项。</summary>
public sealed record WorkspaceContext(
    IReadOnlyList<WorkspaceOption> Options,
    WorkspaceOption? Current)
{
    public static WorkspaceContext Empty { get; } = new([], null);

    /// <summary>保留上下文的 query 片段；没有选中项时为空。</summary>
    public string QuerySuffix => Current is null
        ? string.Empty
        : $"?{WorkspaceContextService.QueryKey}={Uri.EscapeDataString(Current.Key)}";
}

/// <summary>
/// 项目 / 环境上下文的读取与解析。选择走原生 GET 表单，不引入交互态存储；
/// 旧页面不消费上下文，但保留 query 不影响访问。
/// </summary>
public sealed class WorkspaceContextService(IDbContextFactory<GrowthInspectorDbContext> dbFactory)
{
    public const string QueryKey = "workspace";

    public static string? ReadQueryValue(NavigationManager navigation)
    {
        var query = new Uri(navigation.Uri).Query;
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }

        var values = QueryHelpers.ParseQuery(query);
        if (!values.TryGetValue(QueryKey, out var value))
        {
            return null;
        }

        var requested = value.ToString();
        return string.IsNullOrWhiteSpace(requested) ? null : requested;
    }

    public async Task<WorkspaceContext> ResolveAsync(
        string? requestedKey,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var projects = await db.Projects
            .AsNoTracking()
            .Where(x => !x.IsArchived)
            .OrderBy(x => x.Name)
            .Include(x => x.Environments.Where(environment => !environment.IsArchived))
            .ToListAsync(cancellationToken);

        var options = projects
            .SelectMany(project => project.Environments
                .OrderByDescending(environment => environment.IsDefault)
                .ThenBy(environment => environment.Name)
                .Select(environment => new WorkspaceOption(
                    project.Id,
                    project.Name,
                    project.Code,
                    environment.Id,
                    environment.Name,
                    environment.IsDefault)))
            .ToList();

        var current = options.FirstOrDefault(option =>
                string.Equals(option.Key, requestedKey, StringComparison.OrdinalIgnoreCase))
            ?? options.FirstOrDefault(option => option.IsDefaultEnvironment)
            ?? options.FirstOrDefault();

        return new WorkspaceContext(options, current);
    }
}
