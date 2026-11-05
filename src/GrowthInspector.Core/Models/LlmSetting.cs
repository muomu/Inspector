namespace GrowthInspector.Core.Models;

/// <summary>
/// LLM 接入配置（应用级单行，Id 固定为 1）：API key 以密文落库，地址与模型为明文。
/// 缺 key 时视为「未配置」，执行侧明确报错、不伪造结论。
/// </summary>
public sealed class LlmSetting
{
    /// <summary>单行表主键，固定为 1。</summary>
    public int Id { get; set; } = 1;

    /// <summary>DataProtection 加密后的 API key（密文文本）；为空表示未配置密钥。</summary>
    public string? ProtectedApiKey { get; set; }

    /// <summary>OpenAI 兼容的 API base URL。</summary>
    public string? BaseUrl { get; set; }

    /// <summary>执行与摘要共用的模型名。</summary>
    public string? Model { get; set; }

    /// <summary>最后写入时间。</summary>
    public DateTimeOffset? UpdatedAt { get; set; }
}
