namespace GrowthInspector.Agents.Chat;

/// <summary>DeepSeek 调用失败的分类。调用方据此判断能否重试、是否要降级。</summary>
public enum DeepSeekFailureKind
{
    /// <summary>没有配置 DEEPSEEK_API_KEY。</summary>
    MissingApiKey,

    /// <summary>BaseUrl / 模型名等配置不合法。</summary>
    InvalidConfiguration,

    /// <summary>密钥无效或没有权限（HTTP 401 / 403）。</summary>
    Unauthorized,

    /// <summary>被限流（HTTP 429）。</summary>
    RateLimited,

    /// <summary>请求超时（客户端超时或连接超时）。</summary>
    Timeout,

    /// <summary>网络错误（DNS、连接失败等）。</summary>
    Network,

    /// <summary>其它非成功的 HTTP 状态码，具体状态码见 <see cref="DeepSeekRequestException.StatusCode"/>。</summary>
    HttpError,

    /// <summary>响应不是预期的 OpenAI 兼容结构。</summary>
    InvalidResponse,

    /// <summary>客户端本身不可用（例如没有密钥时的占位实现）。</summary>
    ClientUnavailable,
}

/// <summary>
/// 可识别的 DeepSeek 调用异常。不伪造响应内容，调用方拿到异常后自行决定重试或降级。
/// 异常信息里不包含 API key。
/// </summary>
public sealed class DeepSeekRequestException : Exception
{
    public DeepSeekRequestException(
        DeepSeekFailureKind kind,
        string message,
        Exception? innerException = null,
        int? statusCode = null,
        string? responseBodySnippet = null)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
        ResponseBodySnippet = responseBodySnippet;
    }

    public DeepSeekFailureKind Kind { get; }

    /// <summary>非成功响应时的 HTTP 状态码；其它情况为空。</summary>
    public int? StatusCode { get; }

    /// <summary>非成功响应体的前 1 KB，便于排查；不包含请求头与密钥。</summary>
    public string? ResponseBodySnippet { get; }
}
