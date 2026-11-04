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
    /// 注册 agent 层依赖：DeepSeek 执行 / 摘要客户端（无 key 时降级为不可用实现）、
    /// 检查项执行器与 MCP 工具调用入口。
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

        services.AddSingleton<IChecklistItemExecutor, StubChecklistItemExecutor>();
        services.AddSingleton<IMcpServerConnectionFactory>(provider =>
            new McpServerConnectionFactory(
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()));
        services.AddSingleton<IMcpToolInvoker, McpToolInvoker>();

        return services;
    }
}
