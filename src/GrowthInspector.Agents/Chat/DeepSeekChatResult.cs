using Microsoft.Extensions.AI;

namespace GrowthInspector.Agents.Chat;

/// <summary>一次 DeepSeek 调用的用量与耗时。</summary>
public sealed record DeepSeekChatUsage(
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    int? CachedPromptTokens,
    long DurationMs,
    string Model,
    bool ThinkingEnabled);

/// <summary>一次 DeepSeek 调用的完整结果：<see cref="ChatResponse"/> 供 IChatClient 调用方使用，用量供计量。</summary>
public sealed record DeepSeekChatResult(ChatResponse Response, DeepSeekChatUsage Usage);

/// <summary>DeepSeek 客户端自定义字段在 ChatResponse.AdditionalProperties 中的键。</summary>
public static class DeepSeekChatMetadata
{
    /// <summary>值为 <see cref="DeepSeekChatUsage"/>。</summary>
    public const string UsageKey = "deepseek.usage";
}
