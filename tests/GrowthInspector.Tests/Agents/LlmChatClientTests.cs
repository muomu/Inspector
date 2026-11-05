using GrowthInspector.Agents.Chat;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrowthInspector.Tests.Agents;

/// <summary>
/// 惰性 LLM 客户端：缺 key 走可识别异常；同一实例只解析一次快照（在跑任务用旧 key），
/// 新实例重新解析（新任务用新 key）。
/// </summary>
public sealed class LlmChatClientTests
{
    private static readonly DeepSeekOptions FactoryOptions = new()
    {
        BaseUrl = "http://127.0.0.1:9",
        Model = "出厂模型",
        TimeoutSeconds = 2,
    };

    [Fact]
    public async Task MissingApiKey_ThrowsMissingApiKey_AndPointsToSettingsPage()
    {
        var store = new FixedLlmSettingsStore(new LlmSettingsSnapshot(null, "https://api.example.test", "m-1", null));
        using var client = CreateClient(store);

        var exception = await Assert.ThrowsAsync<DeepSeekRequestException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Equal(DeepSeekFailureKind.MissingApiKey, exception.Kind);
        Assert.Contains("设置", exception.Message);
        Assert.Equal(1, store.ReadCount);
    }

    [Fact]
    public async Task Snapshot_IsReadOnce_ForTheLifetimeOfTheClient()
    {
        var store = new FixedLlmSettingsStore(
            new LlmSettingsSnapshot("sk-test-key", "http://127.0.0.1:9", "m-old", null));
        using var client = CreateClient(store);

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "第一次")]));
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "第二次")]));

        // 一个执行会话内不重复读库：运行期间保存新 key 不会改变这一轮使用的 key。
        Assert.Equal(1, store.ReadCount);
    }

    [Fact]
    public async Task NewClientInstance_ResolvesSnapshotAgain()
    {
        var store = new FixedLlmSettingsStore(
            new LlmSettingsSnapshot("sk-test-key", "http://127.0.0.1:9", "m-old", null));

        using (var first = CreateClient(store))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => first.GetResponseAsync([new ChatMessage(ChatRole.User, "旧配置")]));
        }

        store.Snapshot = store.Snapshot with { Model = "m-new" };

        using (var second = CreateClient(store))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => second.GetResponseAsync([new ChatMessage(ChatRole.User, "新配置")]));
        }

        Assert.Equal(2, store.ReadCount);
    }

    [Fact]
    public async Task SavedBaseUrlAndModel_OverrideFactoryDefaults()
    {
        var store = new FixedLlmSettingsStore(
            new LlmSettingsSnapshot("sk-test-key", "not-a-valid-url", "m-new", null));
        using var client = CreateClient(store);

        // 合法地址校验发生在构造真实客户端时：地址来自快照而不是出厂值。
        var exception = await Assert.ThrowsAsync<DeepSeekRequestException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Equal(DeepSeekFailureKind.InvalidConfiguration, exception.Kind);
        Assert.Contains("not-a-valid-url", exception.Message);
    }

    private static LlmChatClient CreateClient(FixedLlmSettingsStore store)
        => new(store, FactoryOptions, DeepSeekClientRole.Execution, NullLogger<LlmChatClient>.Instance);
}
