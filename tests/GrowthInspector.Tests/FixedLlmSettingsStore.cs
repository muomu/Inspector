using GrowthInspector.Core.Abstractions;

namespace GrowthInspector.Tests;

/// <summary>
/// 测试用 LLM 配置存储：直接返回给定快照，并记录读取次数。
/// 用于验证「每个 scope 只解析一次快照」与「新 scope 拿到新配置」这两种热切换语义。
/// </summary>
internal sealed class FixedLlmSettingsStore(LlmSettingsSnapshot snapshot) : ILlmSettingsStore
{
    public LlmSettingsSnapshot Snapshot { get; set; } = snapshot;

    public int ReadCount { get; private set; }

    public Task<LlmSettingsSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        ReadCount++;
        return Task.FromResult(Snapshot);
    }

    public Task SaveAsync(LlmSettingsUpdate update, CancellationToken cancellationToken = default)
    {
        Snapshot = new LlmSettingsSnapshot(
            string.IsNullOrWhiteSpace(update.ApiKey) ? Snapshot.ApiKey : update.ApiKey,
            update.BaseUrl,
            update.Model,
            DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    public Task ClearApiKeyAsync(CancellationToken cancellationToken = default)
    {
        Snapshot = Snapshot with { ApiKey = null };
        return Task.CompletedTask;
    }
}
