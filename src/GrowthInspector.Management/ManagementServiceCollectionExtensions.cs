using GrowthInspector.Core.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GrowthInspector.Management;

/// <summary>
/// 管理层的注册入口：管理 service（UI / 管理 MCP / 反思 agent 共用）与管理 MCP server。
/// 具体 service 与工具面由 TPG-2712 实现，本文件只固定接线点。
/// </summary>
public static class ManagementServiceCollectionExtensions
{
    /// <summary>注册管理 service 与管理 MCP server 的配置（实现由 TPG-2712 填充）。</summary>
    public static IServiceCollection AddGrowthInspectorManagement(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ManagementMcpOptions>(
            configuration.GetSection(ManagementMcpOptions.SectionName));

        return services;
    }

    /// <summary>把管理 MCP server 挂到主进程（Streamable HTTP，仅 loopback）。实现由 TPG-2712 填充。</summary>
    public static WebApplication MapGrowthInspectorManagementMcp(this WebApplication app)
    {
        return app;
    }
}
