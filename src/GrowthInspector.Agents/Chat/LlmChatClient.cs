using GrowthInspector.Core.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GrowthInspector.Agents.Chat;

/// <summary>
/// 按「当前已保存的 LLM 配置」创建客户端的惰性代理：
/// 每次解析（一次检查单执行 / 一次日报生成 = 一个 DI scope）时读取一次设置快照，
/// 因此保存新 key 对新任务立即生效，而已经开始的执行继续用它当时拿到的旧 key 跑完。
/// 缺 key 时降级为 <see cref="UnavailableChatClient"/>，调用方拿到可识别异常后走兜底，不伪造结论。
/// </summary>
public sealed class LlmChatClient : IChatClient
{
    private readonly GrowthInspector.Core.Abstractions.ILlmSettingsStore settingsStore;
    private readonly DeepSeekOptions options;
    private readonly DeepSeekClientRole role;
    private readonly ILogger logger;
    private readonly SemaphoreSlim gate = new(1, 1);

    private IChatClient? resolved;

    public LlmChatClient(
        GrowthInspector.Core.Abstractions.ILlmSettingsStore settingsStore,
        DeepSeekOptions options,
        DeepSeekClientRole role,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        this.settingsStore = settingsStore;
        this.options = options;
        this.role = role;
        this.logger = logger;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var client = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        return await client.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var update in client
            .GetStreamingResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        resolved?.Dispose();
        gate.Dispose();
    }

    /// <summary>解析一次快照；同一实例只解析一次，之后的调用沿用同一个客户端。</summary>
    private async Task<IChatClient> ResolveAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref resolved) is { } ready)
        {
            return ready;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (resolved is null)
            {
                resolved = await CreateAsync(cancellationToken).ConfigureAwait(false);
            }

            return resolved;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IChatClient> CreateAsync(CancellationToken cancellationToken)
    {
        var snapshot = await settingsStore.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.HasApiKey)
        {
            return new UnavailableChatClient(
                "未配置 LLM API key，请到「设置 · 模型接入」填写后再执行。",
                DeepSeekFailureKind.MissingApiKey);
        }

        // 快照里的地址与模型覆盖 appsettings 的出厂值；thinking / 超时仍按配置节。
        var effective = new DeepSeekOptions
        {
            BaseUrl = string.IsNullOrWhiteSpace(snapshot.BaseUrl) ? options.BaseUrl : snapshot.BaseUrl,
            Model = string.IsNullOrWhiteSpace(snapshot.Model) ? options.Model : snapshot.Model,
            ThinkingForExecution = options.ThinkingForExecution,
            ThinkingForSummary = options.ThinkingForSummary,
            TimeoutSeconds = options.TimeoutSeconds,
        };

        logger.LogInformation("使用已保存的 LLM 配置创建 {Role} 客户端（模型 {Model}）。", role, effective.Model);
        return DeepSeekChatClientFactory.CreateClient(effective, snapshot.ApiKey!, role);
    }
}
