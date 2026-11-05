using GrowthInspector.Agents.Chat;
using GrowthInspector.Agents.Execution;
using GrowthInspector.Agents.Mcp;
using GrowthInspector.Agents.Reflection;
using GrowthInspector.Agents.Tools;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Options;
using GrowthInspector.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Agents;

public static class AgentsServiceCollectionExtensions
{
    /// <summary>
    /// 注册 agent 层依赖：DeepSeek 执行 / 摘要客户端（无 key 时降级为不可用实现）、
    /// MCP 工具调用入口、工具基座（本地手册工具 + MCP 透传 + 审计）、
    /// 检查项执行 agent（取证 + 判定）与反思 agent。
    /// </summary>
    public static IServiceCollection AddGrowthInspectorAgents(
        this IServiceCollection services,
        DeepSeekOptions deepSeekOptions)
    {
        var apiKey = DeepSeekChatClientFactory.ReadApiKeyFromEnvironment();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // 缺 key 时进程照常启动：注册显式失败的占位客户端，调用方拿到可识别异常后走兜底。
            services.AddSingleton(_ =>
            {
                var unavailable = new UnavailableChatClient(
                    $"未配置环境变量 {DeepSeekChatClientFactory.ApiKeyEnvironmentVariable}，模型调用不可用。",
                    DeepSeekFailureKind.MissingApiKey);
                return new DeepSeekChatClients(unavailable, unavailable);
            });
        }
        else
        {
            services.AddSingleton(_ => new DeepSeekChatClients(
                DeepSeekChatClientFactory.CreateClient(deepSeekOptions, apiKey, DeepSeekClientRole.Execution),
                DeepSeekChatClientFactory.CreateClient(deepSeekOptions, apiKey, DeepSeekClientRole.Summary)));
        }

        // 单客户端注入点（执行角色）保持骨架阶段的兼容；需要摘要模型时注入 DeepSeekChatClients。
        services.AddSingleton<IChatClient>(provider =>
            provider.GetRequiredService<DeepSeekChatClients>().Execution);

        // MCP 数据源接入：连接工厂 + 工具调用入口（工具审计由调用方经 McpToolInvocationContext 驱动）。
        services.AddSingleton<IMcpServerConnectionFactory>(provider =>
            new McpServerConnectionFactory(
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()));
        services.AddSingleton<IMcpToolInvoker, McpToolInvoker>();

        // 工具基座：手册检索 / 渐进披露 / MCP 透传 / 审计，按 agent 角色装配工具集。
        services.AddSingleton(sp => new RunbookSearchService(
            sp.GetRequiredService<IDbContextFactory<GrowthInspectorDbContext>>(),
            sp.GetRequiredService<IOptions<InspectionOptions>>(),
            sp.GetRequiredService<ILogger<RunbookSearchService>>()));
        services.AddSingleton<ToolCallAuditor>();
        services.AddScoped(sp => new AgentToolsetFactory(
            sp.GetRequiredService<IDbContextFactory<GrowthInspectorDbContext>>(),
            sp.GetRequiredService<IMcpToolInvoker>(),
            sp.GetService<IRunbookWriteService>(),
            sp.GetRequiredService<RunbookSearchService>(),
            sp.GetRequiredService<ToolCallAuditor>(),
            sp.GetRequiredService<IOptions<InspectionOptions>>(),
            sp.GetRequiredService<ILoggerFactory>()));

        // 执行 agent（整单会话 + 取证 + 结构化 claim + 判定）与反思 agent（手册改写）。
        services.AddScoped<IChecklistItemExecutor, ChecklistItemAgentExecutor>();
        services.AddScoped<IReflectionAgent, ReflectionAgent>();

        return services;
    }
}
