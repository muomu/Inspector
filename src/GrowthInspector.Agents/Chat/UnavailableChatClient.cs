using Microsoft.Extensions.AI;

namespace GrowthInspector.Agents.Chat;

/// <summary>
/// 未配置 DEEPSEEK_API_KEY 时的占位 IChatClient：任何调用都会明确抛错，
/// 而不是伪造结论 —— 对应 AC8「LLM 调用失败有兜底且不伪造结论」。
/// </summary>
public sealed class UnavailableChatClient(string reason) : IChatClient
{
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(reason);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(reason);
}
