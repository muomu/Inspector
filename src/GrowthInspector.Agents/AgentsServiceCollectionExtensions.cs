using GrowthInspector.Agents.Chat;
using GrowthInspector.Agents.Execution;
using GrowthInspector.Agents.Mcp;
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
    /// 注册 agent 层依赖：DeepSeek 执行 / 摘要客户端（按当前已保存配置惰性创建，无 key 时降级为不可用实现）、
    /// MCP 工具调用入口、工具基座（本地只读手册工具 + MCP 透传 + 审计 + 同参去重 + 回传截断）
    /// 与检查单执行 agent（整轮一个会话、框架主导逐项推进）。
    /// </summary>
    public static IServiceCollection AddGrowthInspectorAgents(
        this IServiceCollection services,
        DeepSeekOptions deepSeekOptions)
    {
        // 模型接入改为页面配置（全局单 key、加密落库）：客户端按 scope 惰性解析当前配置，
        // 保存后对新任务立即生效，运行中的执行继续用旧 key 跑完；缺 key 时降级为不可用实现。
        services.AddScoped(provider => new DeepSeekChatClients(
            new LlmChatClient(
                provider.GetRequiredService<ILlmSettingsStore>(),
                deepSeekOptions,
                DeepSeekClientRole.Execution,
                provider.GetRequiredService<ILogger<LlmChatClient>>()),
            new LlmChatClient(
                provider.GetRequiredService<ILlmSettingsStore>(),
                deepSeekOptions,
                DeepSeekClientRole.Summary,
                provider.GetRequiredService<ILogger<LlmChatClient>>())));

        // 单客户端注入点（执行角色）；需要摘要角色时注入 DeepSeekChatClients。
        services.AddScoped<IChatClient>(provider =>
            provider.GetRequiredService<DeepSeekChatClients>().Execution);

        // MCP 数据源接入：连接工厂 + 工具调用入口（工具审计由工具基座统一驱动）。
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
            sp.GetRequiredService<RunbookSearchService>(),
            sp.GetRequiredService<ToolCallAuditor>(),
            sp.GetRequiredService<IOptions<InspectionOptions>>(),
            sp.GetRequiredService<ILoggerFactory>()));

        // 执行 agent：整单会话 + 逐项取证 + 结构化 claim。
        services.AddScoped<IChecklistRunAgentExecutor, ChecklistRunAgentExecutor>();

        return services;
    }
}
