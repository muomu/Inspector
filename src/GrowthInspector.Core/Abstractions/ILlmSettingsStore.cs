namespace GrowthInspector.Core.Abstractions;

/// <summary>
/// LLM 接入配置快照。<see cref="ApiKey"/> 是解密后的明文，只在内存中流转，
/// 不允许写日志、不允许出现在异常信息里；对外的读取一律走掩码。
/// </summary>
public sealed record LlmSettingsSnapshot(
    string? ApiKey,
    string? BaseUrl,
    string? Model,
    DateTimeOffset? UpdatedAt)
{
    /// <summary>已保存密钥。</summary>
    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>key / BaseUrl / 模型三项齐全才算已配置。</summary>
    public bool IsConfigured
        => HasApiKey && !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);
}

/// <summary>写入 LLM 接入配置；<see cref="ApiKey"/> 为空表示保持已有密钥不变。</summary>
public sealed record LlmSettingsUpdate(string? ApiKey, string BaseUrl, string Model);

/// <summary>
/// 全局单行的 LLM 接入配置存储（页面是唯一配置源，不再读环境变量）。
/// 读路径负责解密，写路径负责加密；调用方只处理明文快照。
/// </summary>
public interface ILlmSettingsStore
{
    /// <summary>读取当前配置；从未保存时返回各项为空的快照。</summary>
    Task<LlmSettingsSnapshot> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>保存配置；<paramref name="update"/> 的 ApiKey 为空时保留已有密钥。</summary>
    Task SaveAsync(LlmSettingsUpdate update, CancellationToken cancellationToken = default);

    /// <summary>只清除 API key，保留 BaseUrl 与模型（页面据此展示「密钥缺失」）。</summary>
    Task ClearApiKeyAsync(CancellationToken cancellationToken = default);
}
