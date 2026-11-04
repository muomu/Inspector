using GrowthInspector.Core.Options;
using Microsoft.Extensions.AI;

namespace GrowthInspector.Agents.Chat;

/// <summary>按 DeepSeek（OpenAI 兼容）配置创建客户端。</summary>
public static class DeepSeekChatClientFactory
{
    /// <summary>读取本地环境变量中的密钥；不入库、不进 Git。</summary>
    public const string ApiKeyEnvironmentVariable = "DEEPSEEK_API_KEY";

    /// <summary>从环境变量读取密钥；未配置时返回 null。</summary>
    public static string? ReadApiKeyFromEnvironment()
        => Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);

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
