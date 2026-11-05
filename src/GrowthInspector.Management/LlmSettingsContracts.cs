namespace GrowthInspector.Management;

/// <summary>
/// LLM 接入配置的读取视图：key 只回显掩码与末四位，不返回明文；
/// <see cref="DefaultBaseUrl"/> / <see cref="DefaultModel"/> 是 appsettings 的出厂预填值，
/// 首次打开设置页时用它们填充输入框（仍需显式保存才生效）。
/// </summary>
public sealed record LlmSettingsView(
    string? ApiKeyMask,
    string? BaseUrl,
    string? Model,
    bool HasApiKey,
    bool IsConfigured,
    DateTimeOffset? UpdatedAt,
    string DefaultBaseUrl,
    string DefaultModel);

/// <summary>保存 LLM 配置；<see cref="ApiKey"/> 为掩码或空时保持已有 key。</summary>
public sealed record SaveLlmSettingsRequest(string? ApiKey, string BaseUrl, string Model);

/// <summary>测试连接用的表单值；<see cref="ApiKey"/> 为掩码或空时回退到已保存的 key。</summary>
public sealed record TestLlmConnectionRequest(string? ApiKey, string BaseUrl, string Model);

/// <summary>测试连接结果：<see cref="Ok"/> 为 false 时 <see cref="Message"/> 已按原因分类。</summary>
public sealed record LlmConnectionTestResult(bool Ok, string Message, string? Detail);
