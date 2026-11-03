using GrowthInspector.Agents.Chat;
using GrowthInspector.Agents.Execution;
using GrowthInspector.Agents.Mcp;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace GrowthInspector.Agents;

public static class AgentsServiceCollectionExtensions
{
    /// <summary>
    /// 注册 agent 层依赖：DeepSeek IChatClient（无 key 时降级为不可用实现）、
    /// 检查项执行器与 MCP 工具调用的骨架实现。
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

        services.AddSingleton<IChecklistItemExecutor, StubChecklistItemExecutor>();
        services.AddSingleton<IMcpToolInvoker, StubMcpToolInvoker>();

        return services;
    }
}
