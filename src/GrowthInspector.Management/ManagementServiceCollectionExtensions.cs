using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Options;
using GrowthInspector.Management.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Management;

/// <summary>
/// 管理层的注册入口：管理 service（UI / 管理 MCP / 反思 agent 共用）与管理 MCP server。
/// 管理 MCP 挂在主进程的 Streamable HTTP 端点上，按环境变量分级暴露工具，bearer token 鉴权。
/// </summary>
public static class ManagementServiceCollectionExtensions
{
    /// <summary>注册管理 service、后台触发队列与管理 MCP server（含工具分级）。</summary>
    public static IServiceCollection AddGrowthInspectorManagement(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ManagementMcpOptions>(
            configuration.GetSection(ManagementMcpOptions.SectionName));

        var options = configuration
            .GetSection(ManagementMcpOptions.SectionName)
            .Get<ManagementMcpOptions>() ?? new ManagementMcpOptions();

        services.TryAddSingleton<IManagementService, ManagementService>();
        services.TryAddSingleton<IRunbookWriteService>(
            serviceProvider => serviceProvider.GetRequiredService<IManagementService>());

        // 后台触发队列的骨架实现：完整调度（并发 / 互斥 / 启动清理）由 TPG-2706 接管。
        services.TryAddSingleton<ChecklistRunQueue>();
        services.TryAddSingleton<IChecklistRunQueue>(
            serviceProvider => serviceProvider.GetRequiredService<ChecklistRunQueue>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, ChecklistRunQueueHostedService>());

        if (options.Enabled)
        {
            var mcpBuilder = services.AddMcpServer();
            mcpBuilder.WithHttpTransport(transport => transport.Stateless = true);

            // 只读工具永远开放；配置写与管理写按环境变量分开注册，
            // 未开启的档位不会出现在 tools/list 里。
            mcpBuilder.WithTools<ReadTools>();
            if (IsEnvironmentFlagEnabled(options.ConfigWriteEnvironmentVariable))
            {
                mcpBuilder.WithTools<ConfigWriteTools>();
            }

            if (IsEnvironmentFlagEnabled(options.AdminWriteEnvironmentVariable))
            {
                mcpBuilder.WithTools<AdminWriteTools>();
            }
        }

        return services;
    }

    /// <summary>把管理 MCP server 挂到主进程；鉴权中间件先于端点执行。</summary>
    public static WebApplication MapGrowthInspectorManagementMcp(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<ManagementMcpOptions>>().Value;
        if (!options.Enabled)
        {
            return app;
        }

        var path = string.IsNullOrWhiteSpace(options.Path) ? "/mcp" : options.Path;
        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(path),
            branch => branch.Use((context, next) => AuthorizeAsync(context, next, options)));

        app.MapMcp(path);
        return app;
    }

    private static async Task AuthorizeAsync(
        HttpContext context,
        RequestDelegate next,
        ManagementMcpOptions options)
    {
        if (!ManagementMcpRequestGuard.IsAllowedClientAddress(context, options))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync("{\"error\":\"loopback_only\"}");
            return;
        }

        if (!ManagementMcpRequestGuard.IsAllowedHostAndOrigin(context.Request, options))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync("{\"error\":\"origin_or_host_not_allowed\"}");
            return;
        }

        if (!ManagementMcpRequestGuard.HasValidToken(context.Request, options))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync("{\"error\":\"invalid_token\"}");
            return;
        }

        await next(context);
    }

    private static bool IsEnvironmentFlagEnabled(string variableName)
    {
        if (string.IsNullOrWhiteSpace(variableName))
        {
            return false;
        }

        var value = Environment.GetEnvironmentVariable(variableName);
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.Ordinal);
    }
}
