using GrowthInspector.Core.Options;
using Microsoft.Extensions.AI;

namespace GrowthInspector.Agents.Chat;

/// <summary>按 DeepSeek（OpenAI 兼容）配置创建客户端。密钥由调用方从设置页配置快照提供，不读环境变量。</summary>
public static class DeepSeekChatClientFactory
{
    /// <summary>创建执行角色客户端（保留骨架阶段的调用签名）。</summary>
    public static IChatClient Create(DeepSeekOptions options, string apiKey, string? model = null)
        => CreateClient(options, apiKey, DeepSeekClientRole.Execution, modelOverride: model);

    /// <summary>创建指定角色的 DeepSeek 客户端；缺 key 时直接抛可识别异常。</summary>
    public static DeepSeekChatClient CreateClient(
        DeepSeekOptions options,
        string apiKey,
        DeepSeekClientRole role = DeepSeekClientRole.Execution,
        bool echoReasoningContent = false,
        HttpMessageHandler? httpMessageHandler = null,
        string? modelOverride = null)
        => new(options, apiKey, role, echoReasoningContent, httpMessageHandler, modelOverride);
}
