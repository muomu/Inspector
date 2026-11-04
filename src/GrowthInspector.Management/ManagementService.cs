using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Data;
using Microsoft.EntityFrameworkCore;
using EnvironmentModel = GrowthInspector.Core.Models.Environment;

namespace GrowthInspector.Management;

/// <summary>
/// 管理 service 的实现：所有写操作在一个 DbContext 内完成并显式保存；
/// 名称唯一、同 Project 引用与归档过滤在这里统一校验，页面与管理 MCP 共用同一套规则。
/// </summary>
public sealed partial class ManagementService(
    IDbContextFactory<GrowthInspectorDbContext> dbContextFactory,
    IChecklistRunQueue runQueue) : IManagementService
{
    private readonly IDbContextFactory<GrowthInspectorDbContext> dbContextFactory = dbContextFactory;
    private readonly IChecklistRunQueue runQueue = runQueue;

    // ---- 项目 ----

    public async Task<PagedResult<ProjectView>> ListProjectsAsync(
        bool includeArchived = false,
        int limit = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Projects.AsNoTracking()
            .Where(x => includeArchived || !x.IsArchived)
            .OrderBy(x => x.Name)
            .Select(x => new ProjectView(
                x.Id,
                x.Name,
                x.Code,
                x.LarkWebhookUrl,
                x.OpsContext,
                x.IsArchived,
                x.CreatedAt));

        var page = await ManagementRules.PageAsync(query, limit, cursor, cancellationToken);
        return new PagedResult<ProjectView>(
            page.Items.Select(x => x with { LarkWebhookUrl = SecretMask.Mask(x.LarkWebhookUrl) }).ToList(),
            page.NextCursor);
    }

    public async Task<ProjectView?> GetProjectAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return project is null
            ? null
            : new ProjectView(
                project.Id,
                project.Name,
                project.Code,
                SecretMask.Mask(project.LarkWebhookUrl),
                project.OpsContext,
                project.IsArchived,
                project.CreatedAt);
    }

    public async Task<Guid> CreateProjectAsync(CreateProjectRequest request, CancellationToken cancellationToken = default)
    {
        var name = ManagementRules.RequiredText(request.Name, "项目名称", 200);
        var code = ManagementRules.OptionalText(request.Code, 50);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (code is not null && await db.Projects.AnyAsync(x => x.Code == code, cancellationToken))
        {
            throw new ManagementException($"项目标识「{code}」已存在。");
        }

        var project = new Project
        {
            Name = name,
            Code = code,
            LarkWebhookUrl = ManagementRules.OptionalText(request.LarkWebhookUrl, 1000),
            OpsContext = ManagementRules.OptionalText(request.OpsContext, 64 * 1024),
        };

        db.Projects.Add(project);
        await db.SaveChangesAsync(cancellationToken);
        return project.Id;
    }

    public async Task UpdateProjectAsync(Guid id, UpdateProjectRequest request, CancellationToken cancellationToken = default)
    {
        var name = ManagementRules.RequiredText(request.Name, "项目名称", 200);
        var code = ManagementRules.OptionalText(request.Code, 50);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ManagementException("项目不存在。");

        if (code is not null
            && await db.Projects.AnyAsync(x => x.Code == code && x.Id != id, cancellationToken))
        {
            throw new ManagementException($"项目标识「{code}」已存在。");
        }

        project.Name = name;
        project.Code = code;
        project.LarkWebhookUrl = SecretMask.Resolve(
            ManagementRules.OptionalText(request.LarkWebhookUrl, 1000),
            project.LarkWebhookUrl);
        project.OpsContext = ManagementRules.OptionalText(request.OpsContext, 64 * 1024);
        if (request.IsArchived is { } archived)
        {
            project.IsArchived = archived;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetProjectArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ManagementException("项目不存在。");

        project.IsArchived = archived;
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- 环境 ----

    public async Task<PagedResult<EnvironmentView>> ListEnvironmentsAsync(
        Guid? projectId = null,
        bool includeArchived = false,
        int limit = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Environments.AsNoTracking()
            .Where(x => projectId == null || x.ProjectId == projectId)
            .Where(x => includeArchived || !x.IsArchived)
            .OrderBy(x => x.Project!.Name)
            .ThenBy(x => x.Name)
            .Select(x => new EnvironmentView(
                x.Id,
                x.ProjectId,
                x.Project!.Name,
                x.Name,
                x.IsDefault,
                x.IsArchived,
                x.CreatedAt));

        return await ManagementRules.PageAsync(query, limit, cursor, cancellationToken);
    }

    public async Task<EnvironmentView?> GetEnvironmentAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Environments.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new EnvironmentView(
                x.Id,
                x.ProjectId,
                x.Project!.Name,
                x.Name,
                x.IsDefault,
                x.IsArchived,
                x.CreatedAt))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<Guid> CreateEnvironmentAsync(CreateEnvironmentRequest request, CancellationToken cancellationToken = default)
    {
        var name = ManagementRules.RequiredText(request.Name, "环境名称", 100);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Projects.AnyAsync(x => x.Id == request.ProjectId, cancellationToken))
        {
            throw new ManagementException("项目不存在。");
        }

        await EnsureEnvironmentNameAvailableAsync(db, request.ProjectId, name, null, cancellationToken);

        if (request.IsDefault)
        {
            await ClearDefaultEnvironmentAsync(db, request.ProjectId, null, cancellationToken);
        }

        var environment = new EnvironmentModel
        {
            ProjectId = request.ProjectId,
            Name = name,
            IsDefault = request.IsDefault,
        };

        db.Environments.Add(environment);
        await db.SaveChangesAsync(cancellationToken);
        return environment.Id;
    }

    public async Task UpdateEnvironmentAsync(Guid id, UpdateEnvironmentRequest request, CancellationToken cancellationToken = default)
    {
        var name = ManagementRules.RequiredText(request.Name, "环境名称", 100);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var environment = await db.Environments.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ManagementException("环境不存在。");

        await EnsureEnvironmentNameAvailableAsync(db, environment.ProjectId, name, id, cancellationToken);

        if (request.IsDefault == true)
        {
            await ClearDefaultEnvironmentAsync(db, environment.ProjectId, id, cancellationToken);
            environment.IsDefault = true;
        }
        else if (request.IsDefault == false)
        {
            environment.IsDefault = false;
        }

        environment.Name = name;
        if (request.IsArchived is { } archived)
        {
            environment.IsArchived = archived;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetEnvironmentArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var environment = await db.Environments.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ManagementException("环境不存在。");

        environment.IsArchived = archived;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task EnsureEnvironmentNameAvailableAsync(
        GrowthInspectorDbContext db,
        Guid projectId,
        string name,
        Guid? excludeId,
        CancellationToken cancellationToken)
    {
        var exists = await db.Environments.AnyAsync(
            x => x.ProjectId == projectId && x.Name == name && (excludeId == null || x.Id != excludeId),
            cancellationToken);

        if (exists)
        {
            throw new ManagementException($"同一项目下已存在环境「{name}」。");
        }
    }

    private static async Task ClearDefaultEnvironmentAsync(
        GrowthInspectorDbContext db,
        Guid projectId,
        Guid? excludeId,
        CancellationToken cancellationToken)
    {
        var defaults = await db.Environments
            .Where(x => x.ProjectId == projectId && x.IsDefault && (excludeId == null || x.Id != excludeId))
            .ToListAsync(cancellationToken);

        foreach (var item in defaults)
        {
            item.IsDefault = false;
        }
    }
}
