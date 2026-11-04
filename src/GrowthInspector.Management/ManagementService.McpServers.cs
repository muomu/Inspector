using System.Text.Json;
using GrowthInspector.Core.Models;
using GrowthInspector.Data;
using Microsoft.EntityFrameworkCore;

namespace GrowthInspector.Management;

public sealed partial class ManagementService
{
    // ---- MCP server ----

    public async Task<PagedResult<McpServerView>> ListMcpServersAsync(
        Guid? projectId = null,
        bool includeArchived = false,
        int limit = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.McpServers.AsNoTracking()
            .Where(x => projectId == null || x.ProjectId == projectId)
            .Where(x => includeArchived || !x.IsArchived)
            .OrderBy(x => x.Project!.Name)
            .ThenBy(x => x.Name)
            .Select(x => new McpServerView(
                x.Id,
                x.ProjectId,
                x.EnvironmentId,
                x.Environment != null ? x.Environment.Name : null,
                x.Name,
                x.Transport,
                x.Command,
                x.ArgumentsJson,
                x.Url,
                x.IsEnabled,
                x.IsArchived,
                x.TimeoutSeconds,
                x.MaxResultBytes,
                x.CreatedAt));

        var page = await ManagementRules.PageAsync(query, limit, cursor, cancellationToken);
        return new PagedResult<McpServerView>(page.Items.Select(MaskCredentials).ToList(), page.NextCursor);
    }

    public async Task<McpServerView?> GetMcpServerAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var view = await db.McpServers.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new McpServerView(
                x.Id,
                x.ProjectId,
                x.EnvironmentId,
                x.Environment != null ? x.Environment.Name : null,
                x.Name,
                x.Transport,
                x.Command,
                x.ArgumentsJson,
                x.Url,
                x.IsEnabled,
                x.IsArchived,
                x.TimeoutSeconds,
                x.MaxResultBytes,
                x.CreatedAt))
            .SingleOrDefaultAsync(cancellationToken);

        return view is null ? null : MaskCredentials(view);
    }

    public async Task<Guid> CreateMcpServerAsync(CreateMcpServerRequest request, CancellationToken cancellationToken = default)
    {
        var name = ManagementRules.RequiredText(request.Name, "MCP server 名称", 200);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Projects.AnyAsync(x => x.Id == request.ProjectId, cancellationToken))
        {
            throw new ManagementException("项目不存在。");
        }

        await EnsureMcpServerNameAvailableAsync(db, request.ProjectId, name, null, cancellationToken);
        await EnsureEnvironmentBelongsToProjectAsync(db, request.ProjectId, request.EnvironmentId, cancellationToken);

        var command = ManagementRules.OptionalText(request.Command, 2000);
        var argumentsJson = ManagementRules.OptionalJson(request.ArgumentsJson, "启动参数", JsonValueKind.Array);
        var url = ManagementRules.OptionalText(request.Url, 2000);
        ValidateTransport(request.Transport, command, url);
        ValidateLimits(request.TimeoutSeconds, request.MaxResultBytes);

        var server = new McpServer
        {
            ProjectId = request.ProjectId,
            EnvironmentId = request.EnvironmentId,
            Name = name,
            Transport = request.Transport,
            Command = request.Transport == McpTransport.Stdio ? command : null,
            ArgumentsJson = request.Transport == McpTransport.Stdio ? argumentsJson : null,
            Url = request.Transport == McpTransport.Http ? url : null,
            IsEnabled = request.IsEnabled,
            TimeoutSeconds = request.TimeoutSeconds,
            MaxResultBytes = request.MaxResultBytes,
        };

        db.McpServers.Add(server);
        await db.SaveChangesAsync(cancellationToken);
        return server.Id;
    }

    public async Task UpdateMcpServerAsync(Guid id, UpdateMcpServerRequest request, CancellationToken cancellationToken = default)
    {
        var name = ManagementRules.RequiredText(request.Name, "MCP server 名称", 200);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.McpServers.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ManagementException("MCP server 不存在。");

        await EnsureMcpServerNameAvailableAsync(db, server.ProjectId, name, id, cancellationToken);
        await EnsureEnvironmentBelongsToProjectAsync(db, server.ProjectId, request.EnvironmentId, cancellationToken);

        var command = SecretMask.Resolve(
            ManagementRules.OptionalText(request.Command, 2000),
            server.Command);
        // 掩码值本身不是合法 JSON，先判断「保持原值」再校验新值。
        var argumentsJson = string.IsNullOrWhiteSpace(request.ArgumentsJson) || SecretMask.IsMasked(request.ArgumentsJson)
            ? server.ArgumentsJson
            : ManagementRules.OptionalJson(request.ArgumentsJson, "启动参数", JsonValueKind.Array);
        var url = SecretMask.Resolve(
            ManagementRules.OptionalText(request.Url, 2000),
            server.Url);

        ValidateTransport(request.Transport, command, url);
        var timeoutSeconds = request.TimeoutSeconds ?? server.TimeoutSeconds;
        var maxResultBytes = request.MaxResultBytes ?? server.MaxResultBytes;
        ValidateLimits(timeoutSeconds, maxResultBytes);

        server.Name = name;
        server.Transport = request.Transport;
        server.EnvironmentId = request.EnvironmentId;
        server.Command = request.Transport == McpTransport.Stdio ? command : null;
        server.ArgumentsJson = request.Transport == McpTransport.Stdio ? argumentsJson : null;
        server.Url = request.Transport == McpTransport.Http ? url : null;
        server.TimeoutSeconds = timeoutSeconds;
        server.MaxResultBytes = maxResultBytes;
        if (request.IsEnabled is { } enabled)
        {
            server.IsEnabled = enabled;
        }

        if (request.IsArchived is { } archived)
        {
            server.IsArchived = archived;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetMcpServerArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.McpServers.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ManagementException("MCP server 不存在。");

        server.IsArchived = archived;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static McpServerView MaskCredentials(McpServerView view)
        => view with
        {
            Command = SecretMask.Mask(view.Command),
            ArgumentsJson = SecretMask.Mask(view.ArgumentsJson),
            Url = SecretMask.Mask(view.Url),
        };

    private static async Task EnsureMcpServerNameAvailableAsync(
        GrowthInspectorDbContext db,
        Guid projectId,
        string name,
        Guid? excludeId,
        CancellationToken cancellationToken)
    {
        var exists = await db.McpServers.AnyAsync(
            x => x.ProjectId == projectId && x.Name == name && (excludeId == null || x.Id != excludeId),
            cancellationToken);

        if (exists)
        {
            throw new ManagementException($"同一项目下已存在 MCP server「{name}」。");
        }
    }

    private static async Task EnsureEnvironmentBelongsToProjectAsync(
        GrowthInspectorDbContext db,
        Guid projectId,
        Guid? environmentId,
        CancellationToken cancellationToken)
    {
        if (environmentId is null)
        {
            return;
        }

        var environment = await db.Environments
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == environmentId, cancellationToken)
            ?? throw new ManagementException("绑定的环境不存在。");

        if (environment.ProjectId != projectId)
        {
            throw new ManagementException("绑定环境与 MCP server 不属于同一 Project。");
        }
    }

    private static void ValidateTransport(McpTransport transport, string? command, string? url)
    {
        if (!Enum.IsDefined(transport))
        {
            throw new ManagementException("传输方式无效。");
        }

        if (transport == McpTransport.Stdio && string.IsNullOrWhiteSpace(command))
        {
            throw new ManagementException("stdio 传输必须填写启动命令。");
        }

        if (transport == McpTransport.Http)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new ManagementException("HTTP 传输必须填写 URL。");
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ManagementException("HTTP 传输的 URL 必须是 http / https 绝对地址。");
            }
        }
    }

    private static void ValidateLimits(int timeoutSeconds, int maxResultBytes)
    {
        if (timeoutSeconds <= 0)
        {
            throw new ManagementException("超时时间必须大于 0 秒。");
        }

        if (maxResultBytes <= 0)
        {
            throw new ManagementException("结果截断阈值必须大于 0 字节。");
        }
    }
}
