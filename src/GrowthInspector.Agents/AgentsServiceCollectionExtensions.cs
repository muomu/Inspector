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
    /// 注册 agent 层依赖：DeepSeek IChatClient（无 key 时降级为不可用实现）、
    /// 工具基座（本地手册工具 + MCP 透传 + 审计）与检查项执行器。
    /// </summary>
    public static IServiceCollection AddGrowthInspectorAgents(
        this IServiceCollection services,
        DeepSeekOptions deepSeekOptions)
    {
        var apiKey = Environment.GetEnvironmentVariable(DeepSeekChatClientFactory.ApiKeyEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            services.AddSingleton<IChatClient>(_ => new UnavailableChatClient(
                $"未配置环境变量 {DeepSeekChatClientFactory.ApiKeyEnvironmentVariable}，模型调用不可用。"));
        }
        else
        {
            services.AddSingleton(_ => DeepSeekChatClientFactory.Create(deepSeekOptions, apiKey));
        }

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

        services.AddScoped<IChecklistItemExecutor, ChecklistItemAgentExecutor>();
        services.AddScoped<IReflectionAgent, ReflectionAgent>();
        services.AddSingleton<IMcpToolInvoker, StubMcpToolInvoker>();

        return services;
    }
}
