using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GrowthInspector.Core.Options;
using Microsoft.Extensions.AI;

namespace GrowthInspector.Agents.Chat;

/// <summary>
/// DeepSeek（OpenAI 兼容）客户端的最小实现：
/// 直接向 /chat/completions 发请求，以便显式控制 thinking 开关、reasoning_content 回传与 token 计量。
/// 密钥只从构造函数传入（由工厂从环境变量读取），不写入日志与异常信息。
/// </summary>
public sealed class DeepSeekChatClient : IChatClient
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly TimeSpan _timeout;
    private readonly bool _echoReasoningContent;
    private readonly bool _thinkingDefault;

    /// <param name="options">DeepSeek 接入配置。</param>
    /// <param name="apiKey">API key，只允许来自环境变量。</param>
    /// <param name="role">客户端用途，决定默认模型与默认 thinking 开关。</param>
    /// <param name="echoReasoningContent">
    /// 多轮请求时是否把历史 assistant 轮次的 reasoning_content 一并回传。v1 默认关闭；
    /// 打开后，历史消息里的 <see cref="TextReasoningContent"/> 会写回请求体。
    /// </param>
    /// <param name="httpMessageHandler">测试用的 HTTP 处理器；为空时使用默认实现。</param>
    /// <param name="modelOverride">显式指定的模型名，为空时按 role 从配置取。</param>
    public DeepSeekChatClient(
        DeepSeekOptions options,
        string apiKey,
        DeepSeekClientRole role = DeepSeekClientRole.Execution,
        bool echoReasoningContent = false,
        HttpMessageHandler? httpMessageHandler = null,
        string? modelOverride = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new DeepSeekRequestException(
                DeepSeekFailureKind.MissingApiKey,
                "未配置 LLM API key，请到「设置 · 模型接入」填写后再执行。");
        }

        if (!Uri.TryCreate(NormalizeBaseUrl(options.BaseUrl), UriKind.Absolute, out var baseUri))
        {
            throw new DeepSeekRequestException(
                DeepSeekFailureKind.InvalidConfiguration,
                $"DeepSeek BaseUrl 不是合法的绝对地址：{options.BaseUrl}");
        }

        _apiKey = apiKey;
        _echoReasoningContent = echoReasoningContent;
        _thinkingDefault = role == DeepSeekClientRole.Summary
            ? options.ThinkingForSummary
            : options.ThinkingForExecution;

        _model = string.IsNullOrWhiteSpace(modelOverride) ? options.Model : modelOverride;
        if (string.IsNullOrWhiteSpace(_model))
        {
            _model = options.Model;
        }

        var timeoutSeconds = options.TimeoutSeconds > 0 ? options.TimeoutSeconds : 120;
        _timeout = TimeSpan.FromSeconds(timeoutSeconds);

        _httpClient = httpMessageHandler is null ? new HttpClient() : new HttpClient(httpMessageHandler);
        _httpClient.BaseAddress = baseUri;
        // 超时由每次请求自己的 CTS 控制，避免 HttpClient 自身的 100 秒默认值先触发。
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>当前客户端实际使用的模型。</summary>
    public string Model => _model;

    /// <summary>执行一次非流式对话，返回响应与用量。</summary>
    public async Task<DeepSeekChatResult> GetResponseWithUsageAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        if (messageList.Count == 0)
        {
            throw new ArgumentException("至少需要一条消息。", nameof(messages));
        }

        var thinkingEnabled = ResolveThinkingEnabled(options);
        var requestBody = BuildRequestBody(messageList, options, thinkingEnabled);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(
                requestBody.ToJsonString(s_jsonOptions),
                Encoding.UTF8,
                "application/json");

            using var httpResponse = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token);
            var responseBody = await httpResponse.Content.ReadAsStringAsync(timeoutSource.Token);

            if (!httpResponse.IsSuccessStatusCode)
            {
                throw CreateHttpFailure(httpResponse.StatusCode, responseBody);
            }

            var parsed = ParseResponse(responseBody);
            var durationMs = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            var usage = new DeepSeekChatUsage(
                parsed.PromptTokens,
                parsed.CompletionTokens,
                parsed.TotalTokens,
                parsed.CachedPromptTokens,
                durationMs,
                parsed.Model ?? _model,
                thinkingEnabled);

            var response = parsed.Response;
            response.Usage = new UsageDetails
            {
                InputTokenCount = usage.PromptTokens,
                OutputTokenCount = usage.CompletionTokens,
                TotalTokenCount = usage.TotalTokens,
                CachedInputTokenCount = usage.CachedPromptTokens,
            };
            (response.AdditionalProperties ??= []).Add(DeepSeekChatMetadata.UsageKey, usage);

            return new DeepSeekChatResult(response, usage);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DeepSeekRequestException(
                DeepSeekFailureKind.Timeout,
                $"DeepSeek 请求超时（{_timeout.TotalSeconds:0} 秒）。",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new DeepSeekRequestException(
                DeepSeekFailureKind.Network,
                $"DeepSeek 网络请求失败：{exception.Message}",
                exception);
        }
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = await GetResponseWithUsageAsync(messages, options, cancellationToken);
        return result.Response;
    }

    /// <summary>
    /// v1 不实现真正的流式解析；这里把一次非流式响应包装成单个更新，保证 IChatClient 调用方可用。
    /// 调用方需要逐 token 增量时再补 SSE 解析。
    /// </summary>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await GetResponseWithUsageAsync(messages, options, cancellationToken);
        var response = result.Response;
        var assistant = response.Messages.FirstOrDefault()
            ?? new ChatMessage(ChatRole.Assistant, string.Empty);

        yield return new ChatResponseUpdate(ChatRole.Assistant, assistant.Contents)
        {
            ResponseId = response.ResponseId,
            ModelId = response.ModelId,
            CreatedAt = response.CreatedAt,
            FinishReason = response.FinishReason,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [DeepSeekChatMetadata.UsageKey] = result.Usage,
            },
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType == typeof(IChatClient) ? this : null;

    public void Dispose() => _httpClient.Dispose();

    /// <summary>构建 OpenAI 兼容的请求体，包含 DeepSeek 的 thinking 扩展字段。</summary>
    private JsonObject BuildRequestBody(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        bool thinkingEnabled)
    {
        var payload = new JsonObject
        {
            ["model"] = _model,
            ["messages"] = BuildMessages(messages),
            ["stream"] = false,
            ["thinking"] = new JsonObject { ["type"] = thinkingEnabled ? "enabled" : "disabled" },
        };

        if (options?.Temperature is { } temperature)
        {
            payload["temperature"] = temperature;
        }

        if (options?.MaxOutputTokens is { } maxTokens)
        {
            payload["max_tokens"] = maxTokens;
        }

        if (options?.TopP is { } topP)
        {
            payload["top_p"] = topP;
        }

        if (options?.StopSequences is { Count: > 0 } stopSequences)
        {
            var stop = new JsonArray();
            foreach (var sequence in stopSequences)
            {
                stop.Add(sequence);
            }

            payload["stop"] = stop;
        }

        if (options?.Seed is { } seed)
        {
            payload["seed"] = seed;
        }

        if (options?.Tools is { Count: > 0 } tools)
        {
            var definitions = BuildToolDefinitions(tools);
            if (definitions.Count > 0)
            {
                payload["tools"] = definitions;
                payload["tool_choice"] = BuildToolChoice(options.ToolMode);
            }
        }

        return payload;
    }

    private JsonArray BuildMessages(IReadOnlyList<ChatMessage> messages)
    {
        var result = new JsonArray();
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.Tool)
            {
                AppendToolResultMessages(result, message);
                continue;
            }

            var textParts = message.Contents.OfType<TextContent>().Select(content => content.Text).ToList();
            var messageNode = new JsonObject { ["role"] = message.Role.Value };
            if (textParts.Count > 0)
            {
                messageNode["content"] = string.Concat(textParts);
            }
            else if (!string.IsNullOrEmpty(message.Text))
            {
                messageNode["content"] = message.Text;
            }

            if (message.Role == ChatRole.Assistant)
            {
                if (_echoReasoningContent)
                {
                    var reasoning = string.Concat(
                        message.Contents.OfType<TextReasoningContent>().Select(content => content.Text));
                    if (string.IsNullOrEmpty(reasoning)
                        && message.AdditionalProperties?.TryGetValue("reasoning_content", out var rawReasoning) == true
                        && rawReasoning is string rawReasoningText)
                    {
                        reasoning = rawReasoningText;
                    }

                    if (!string.IsNullOrEmpty(reasoning))
                    {
                        messageNode["reasoning_content"] = reasoning;
                    }
                }

                var toolCalls = message.Contents.OfType<FunctionCallContent>().ToList();
                if (toolCalls.Count > 0)
                {
                    var callsNode = new JsonArray();
                    foreach (var toolCall in toolCalls)
                    {
                        callsNode.Add(new JsonObject
                        {
                            ["id"] = toolCall.CallId,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = toolCall.Name,
                                ["arguments"] = JsonSerializer.Serialize(toolCall.Arguments, s_jsonOptions),
                            },
                        });
                    }

                    messageNode["tool_calls"] = callsNode;
                }
            }

            result.Add(messageNode);
        }

        return result;
    }

    private static void AppendToolResultMessages(JsonArray result, ChatMessage message)
    {
        var results = message.Contents.OfType<FunctionResultContent>().ToList();
        if (results.Count == 0)
        {
            result.Add(new JsonObject
            {
                ["role"] = ChatRole.Tool.Value,
                ["content"] = message.Text ?? string.Empty,
            });
            return;
        }

        foreach (var functionResult in results)
        {
            result.Add(new JsonObject
            {
                ["role"] = ChatRole.Tool.Value,
                ["tool_call_id"] = functionResult.CallId,
                ["content"] = ResultToText(functionResult.Result),
            });
        }
    }

    private static JsonArray BuildToolDefinitions(IList<AITool> tools)
    {
        var definitions = new JsonArray();
        foreach (var tool in tools)
        {
            if (tool is not AIFunctionDeclaration function || string.IsNullOrWhiteSpace(function.Name))
            {
                continue;
            }

            JsonNode parameters;
            try
            {
                parameters = function.JsonSchema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                    ? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                    : JsonNode.Parse(function.JsonSchema.GetRawText()) ?? new JsonObject();
            }
            catch (JsonException)
            {
                parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
            }

            definitions.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = function.Name,
                    ["description"] = function.Description,
                    ["parameters"] = parameters,
                },
            });
        }

        return definitions;
    }

    private static JsonNode BuildToolChoice(ChatToolMode? toolMode)
    {
        if (toolMode is RequiredChatToolMode required && !string.IsNullOrWhiteSpace(required.RequiredFunctionName))
        {
            return new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = required.RequiredFunctionName },
            };
        }

        return toolMode is RequiredChatToolMode
            ? JsonValue.Create("required")!
            : JsonValue.Create("auto")!;
    }

    /// <summary>
    /// 调用方显式传入 ChatOptions.Reasoning 时以它为准（映射到 DeepSeek 的 thinking 开关），
    /// 否则按 role 使用配置里的 ThinkingForExecution / ThinkingForSummary。
    /// </summary>
    private bool ResolveThinkingEnabled(ChatOptions? options)
    {
        var reasoning = options?.Reasoning;
        if (reasoning is null)
        {
            return _thinkingDefault;
        }

        var hasExplicitSignal = reasoning.Output.HasValue || reasoning.Effort.HasValue;
        if (!hasExplicitSignal)
        {
            return _thinkingDefault;
        }

        return reasoning.Output is ReasoningOutput.Summary or ReasoningOutput.Full
            || reasoning.Effort is ReasoningEffort.Low or ReasoningEffort.Medium or ReasoningEffort.High or ReasoningEffort.ExtraHigh;
    }

    private static DeepSeekRequestException CreateHttpFailure(HttpStatusCode statusCode, string responseBody)
    {
        var snippet = Truncate(responseBody, 1024);
        var kind = (int)statusCode switch
        {
            401 or 403 => DeepSeekFailureKind.Unauthorized,
            429 => DeepSeekFailureKind.RateLimited,
            _ => DeepSeekFailureKind.HttpError,
        };

        var message = kind switch
        {
            DeepSeekFailureKind.Unauthorized => "DeepSeek 拒绝访问：API key 无效或没有权限。",
            DeepSeekFailureKind.RateLimited => "DeepSeek 返回 429，请求被限流。",
            _ => $"DeepSeek 返回 HTTP {(int)statusCode}。",
        };

        return new DeepSeekRequestException(kind, message, statusCode: (int)statusCode, responseBodySnippet: snippet);
    }

    private ParsedResponse ParseResponse(string responseBody)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(responseBody);
        }
        catch (JsonException exception)
        {
            throw new DeepSeekRequestException(
                DeepSeekFailureKind.InvalidResponse,
                "DeepSeek 响应不是合法 JSON。",
                exception,
                responseBodySnippet: Truncate(responseBody, 1024));
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                throw new DeepSeekRequestException(
                    DeepSeekFailureKind.InvalidResponse,
                    "DeepSeek 响应缺少 choices。",
                    responseBodySnippet: Truncate(responseBody, 1024));
            }

            var choice = choices[0];
            if (!choice.TryGetProperty("message", out var messageElement))
            {
                throw new DeepSeekRequestException(
                    DeepSeekFailureKind.InvalidResponse,
                    "DeepSeek 响应缺少 message。",
                    responseBodySnippet: Truncate(responseBody, 1024));
            }

            var contents = new List<AIContent>();
            if (messageElement.TryGetProperty("content", out var contentElement)
                && contentElement.ValueKind == JsonValueKind.String)
            {
                var content = contentElement.GetString();
                if (!string.IsNullOrEmpty(content))
                {
                    contents.Add(new TextContent(content));
                }
            }

            if (messageElement.TryGetProperty("reasoning_content", out var reasoningElement)
                && reasoningElement.ValueKind == JsonValueKind.String)
            {
                var reasoning = reasoningElement.GetString();
                if (!string.IsNullOrEmpty(reasoning))
                {
                    contents.Add(new TextReasoningContent(reasoning));
                }
            }

            if (messageElement.TryGetProperty("tool_calls", out var toolCallsElement)
                && toolCallsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var toolCallElement in toolCallsElement.EnumerateArray())
                {
                    var callId = toolCallElement.TryGetProperty("id", out var idElement)
                        ? idElement.GetString() ?? string.Empty
                        : string.Empty;
                    if (!toolCallElement.TryGetProperty("function", out var functionElement))
                    {
                        continue;
                    }

                    var name = functionElement.TryGetProperty("name", out var nameElement)
                        ? nameElement.GetString() ?? string.Empty
                        : string.Empty;
                    var arguments = functionElement.TryGetProperty("arguments", out var argumentsElement)
                        ? ParseArguments(argumentsElement)
                        : new Dictionary<string, object?>();
                    contents.Add(new FunctionCallContent(callId, name, arguments!));
                }
            }

            var assistantMessage = new ChatMessage(ChatRole.Assistant, contents);
            var response = new ChatResponse(assistantMessage)
            {
                ResponseId = TryGetString(root, "id"),
                ModelId = TryGetString(root, "model") ?? _model,
                CreatedAt = TryGetCreatedAt(root),
                FinishReason = ParseFinishReason(choice),
            };

            var parsed = new ParsedResponse(response, _model);
            if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
            {
                parsed.PromptTokens = TryGetInt(usageElement, "prompt_tokens");
                parsed.CompletionTokens = TryGetInt(usageElement, "completion_tokens");
                parsed.TotalTokens = TryGetInt(usageElement, "total_tokens");
                if (parsed.TotalTokens == 0)
                {
                    parsed.TotalTokens = parsed.PromptTokens + parsed.CompletionTokens;
                }

                if (usageElement.TryGetProperty("prompt_cache_hit_tokens", out var cacheHitElement)
                    && cacheHitElement.TryGetInt32(out var cacheHitTokens))
                {
                    parsed.CachedPromptTokens = cacheHitTokens;
                }
                else if (usageElement.TryGetProperty("prompt_tokens_details", out var detailsElement)
                         && detailsElement.ValueKind == JsonValueKind.Object
                         && detailsElement.TryGetProperty("cached_tokens", out var cachedElement)
                         && cachedElement.TryGetInt32(out var cachedTokens))
                {
                    parsed.CachedPromptTokens = cachedTokens;
                }
            }

            return parsed;
        }
    }

    private static IDictionary<string, object?>? ParseArguments(JsonElement argumentsElement)
    {
        if (argumentsElement.ValueKind != JsonValueKind.String)
        {
            return new Dictionary<string, object?>();
        }

        var raw = argumentsElement.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Dictionary<string, object?>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(raw, s_jsonOptions)
                ?? new Dictionary<string, object?>();
        }
        catch (JsonException)
        {
            // 模型偶尔会返回不完整的 JSON；保留原文，交给上层判断。
            return new Dictionary<string, object?> { ["_raw"] = raw };
        }
    }

    private static ChatFinishReason? ParseFinishReason(JsonElement choice)
    {
        if (!choice.TryGetProperty("finish_reason", out var finishReasonElement)
            || finishReasonElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return finishReasonElement.GetString() switch
        {
            "stop" => ChatFinishReason.Stop,
            "length" => ChatFinishReason.Length,
            "tool_calls" => ChatFinishReason.ToolCalls,
            "content_filter" => ChatFinishReason.ContentFilter,
            _ => null,
        };
    }

    private static DateTimeOffset? TryGetCreatedAt(JsonElement root)
    {
        if (root.TryGetProperty("created", out var createdElement)
            && createdElement.TryGetInt64(out var unixSeconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        return null;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int TryGetInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value) ? value : 0;

    private static string ResultToText(object? result)
        => result switch
        {
            null => string.Empty,
            string text => text,
            JsonElement jsonElement => jsonElement.GetRawText(),
            _ => JsonSerializer.Serialize(result, s_jsonOptions),
        };

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static string NormalizeBaseUrl(string? baseUrl)
    {
        var trimmed = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.deepseek.com" : baseUrl.Trim();
        return trimmed.EndsWith('/') ? trimmed : trimmed + "/";
    }

    /// <summary>解析中间产物，避免把 JsonDocument 生命周期带出方法。</summary>
    private sealed class ParsedResponse(ChatResponse response, string model)
    {
        public ChatResponse Response { get; } = response;

        public string Model { get; } = model;

        public int PromptTokens { get; set; }

        public int CompletionTokens { get; set; }

        public int TotalTokens { get; set; }

        public int? CachedPromptTokens { get; set; }
    }
}
