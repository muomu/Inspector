using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GrowthInspector.Management;

/// <summary>
/// 「测试连接」探针：用表单里的 BaseUrl / 模型 / key 发一次最小 chat 请求，
/// 一次验证三件事（地址可达、key 有效、模型名被接受）。失败按原因分类成人话，不改动已保存的配置。
/// </summary>
public sealed class LlmConnectionProbe(HttpMessageHandler? httpMessageHandler = null)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);
    private const int SnippetLength = 300;

    /// <summary>
    /// 试连；<paramref name="apiKey"/> 只用于本次请求，不写日志、不进异常信息。
    /// </summary>
    public async Task<LlmConnectionTestResult> TestAsync(
        string apiKey,
        string baseUrl,
        string model,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new LlmConnectionTestResult(false, "API key 不能为空：请填写 key 后再测试。", null);
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            return new LlmConnectionTestResult(false, "BaseUrl 必须是 http(s) 绝对地址。", null);
        }

        var endpoint = new Uri(baseUri, "chat/completions");
        var payload = new
        {
            model,
            messages = new[] { new { role = "user", content = "ping" } },
            max_tokens = 1,
            stream = false,
        };

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(ProbeTimeout);
        var startedAt = Stopwatch.GetTimestamp();

        using var client = httpMessageHandler is null ? new HttpClient() : new HttpClient(httpMessageHandler);
        client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload, s_jsonOptions),
                Encoding.UTF8,
                "application/json");

            using var response = await client.SendAsync(request, timeoutSource.Token);
            var body = await response.Content.ReadAsStringAsync(timeoutSource.Token);
            var elapsed = Stopwatch.GetElapsedTime(startedAt);

            if (response.IsSuccessStatusCode)
            {
                return new LlmConnectionTestResult(
                    true,
                    $"连接成功（模型 {model}，{elapsed.TotalMilliseconds:F0} ms）。",
                    null);
            }

            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    => new(false, "key 无效或没有权限（HTTP 401 / 403）：请检查 API key。", Snippet(body)),
                HttpStatusCode.NotFound or HttpStatusCode.BadRequest
                    => new(false, $"BaseUrl 或模型名不被接受（HTTP {(int)response.StatusCode}）：请检查地址与模型名。", Snippet(body)),
                HttpStatusCode.TooManyRequests
                    => new(false, "被限流（HTTP 429）：稍后再试。", Snippet(body)),
                _ => new(false, $"请求失败（HTTP {(int)response.StatusCode}）。", Snippet(body)),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LlmConnectionTestResult(false, "连接超时（15 秒）：请检查 BaseUrl 是否可达。", null);
        }
        catch (HttpRequestException ex)
        {
            return new LlmConnectionTestResult(false, "无法连接 BaseUrl（DNS 或网络错误）。", Snippet(ex.Message));
        }
    }

    private static string? Snippet(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var trimmed = body.Trim();
        return trimmed.Length <= SnippetLength ? trimmed : trimmed[..SnippetLength] + "…";
    }
}
