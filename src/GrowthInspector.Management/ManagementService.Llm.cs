using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Options;

namespace GrowthInspector.Management;

/// <summary>
/// 模型接入（LLM API key）的读写与试连：与项目 webhook、MCP 凭据共用同一套 write-only 掩码语义，
/// key 加密由 <see cref="LlmSettingsStore"/> 负责，本层只做必填 / 地址校验与明文快照流转。
/// </summary>
public sealed partial class ManagementService
{
    /// <summary>读取当前 LLM 配置（key 已掩码），并带上 appsettings 里的出厂预填值。</summary>
    public async Task<LlmSettingsView> GetLlmSettingsAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await llmSettingsStore.GetAsync(cancellationToken);
        var defaults = deepSeekOptions.Value;

        return new LlmSettingsView(
            SecretMask.Mask(snapshot.ApiKey),
            snapshot.BaseUrl,
            snapshot.Model,
            snapshot.HasApiKey,
            snapshot.IsConfigured,
            snapshot.UpdatedAt,
            defaults.BaseUrl,
            defaults.Model);
    }

    /// <summary>保存配置：三字段必填；key 传掩码或留空表示沿用已保存的 key。</summary>
    public async Task SaveLlmSettingsAsync(
        SaveLlmSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var baseUrl = RequireHttpBaseUrl(request.BaseUrl);
        var model = ManagementRules.RequiredText(request.Model, "模型名", 200);
        var existing = await llmSettingsStore.GetAsync(cancellationToken);
        var apiKey = SecretMask.Resolve(ManagementRules.OptionalText(request.ApiKey, 1000), existing.ApiKey);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ManagementException("API key 不能为空：请填写 key 后再保存。");
        }

        await llmSettingsStore.SaveAsync(new LlmSettingsUpdate(apiKey, baseUrl, model), cancellationToken);
    }

    /// <summary>只清除 API key，保留 BaseUrl 与模型（页面回退到「密钥缺失」）。</summary>
    public Task ClearLlmApiKeyAsync(CancellationToken cancellationToken = default)
        => llmSettingsStore.ClearApiKeyAsync(cancellationToken);

    /// <summary>用当前表单值试连一次；不改动已保存的配置。</summary>
    public async Task<LlmConnectionTestResult> TestLlmConnectionAsync(
        TestLlmConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var baseUrl = RequireHttpBaseUrl(request.BaseUrl);
        var model = ManagementRules.RequiredText(request.Model, "模型名", 200);
        var existing = await llmSettingsStore.GetAsync(cancellationToken);
        var apiKey = SecretMask.Resolve(ManagementRules.OptionalText(request.ApiKey, 1000), existing.ApiKey);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ManagementException("API key 不能为空：请填写 key 后再测试连接。");
        }

        return await llmConnectionProbe.TestAsync(apiKey, baseUrl, model, cancellationToken);
    }

    private static string RequireHttpBaseUrl(string? value)
    {
        var baseUrl = ManagementRules.RequiredText(value, "BaseUrl", 500);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ManagementException("BaseUrl 必须是 http(s) 绝对地址，例如 https://api.deepseek.com。");
        }

        return baseUrl;
    }
}
