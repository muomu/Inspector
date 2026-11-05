using System.Net;
using System.Text;
using System.Text.Json;
using GrowthInspector.Agents;
using GrowthInspector.Agents.Chat;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GrowthInspector.Tests;

/// <summary>DeepSeek 客户端：配置解析、thinking 透传、reasoning_content 回传、token 计量与失败语义。</summary>
public sealed class DeepSeekChatClientTests
{
    private const string SuccessJson = """
        {
          "id": "chatcmpl-test",
          "object": "chat.completion",
          "created": 1730000000,
          "model": "deepseek-chat",
          "choices": [
            {
              "index": 0,
              "message": {
                "role": "assistant",
                "content": "巡检结论",
                "reasoning_content": "先看指标再下结论"
              },
              "finish_reason": "stop"
            }
          ],
          "usage": {
            "prompt_tokens": 12,
            "completion_tokens": 34,
            "total_tokens": 46,
            "prompt_cache_hit_tokens": 5
          }
        }
        """;

    [Fact]
    public async Task ExecutionClient_UsesConfiguredModelAndDisabledThinking_AndMetersTokens()
    {
        var handler = new RecordingHandler(SuccessJson);
        var options = CreateOptions();
        var client = new DeepSeekChatClient(options, "test-key", DeepSeekClientRole.Execution, httpMessageHandler: handler);

        var result = await client.GetResponseWithUsageAsync([new ChatMessage(ChatRole.User, "请检查")]);

        using var request = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("deepseek-exec", request.RootElement.GetProperty("model").GetString());
        Assert.Equal("disabled", request.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(request.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("Bearer test-key", handler.LastAuthorization);
        Assert.Equal("巡检结论", result.Response.Text);
        Assert.Contains(result.Response.Messages[0].Contents, content => content is TextReasoningContent { Text: "先看指标再下结论" });
        Assert.Equal(12, result.Usage.PromptTokens);
        Assert.Equal(34, result.Usage.CompletionTokens);
        Assert.Equal(46, result.Usage.TotalTokens);
        Assert.Equal(5, result.Usage.CachedPromptTokens);
        Assert.False(result.Usage.ThinkingEnabled);
        Assert.True(result.Usage.DurationMs >= 0);
        Assert.Equal(12, result.Response.Usage!.InputTokenCount);
        Assert.Equal(34, result.Response.Usage.OutputTokenCount);
        Assert.Equal("chatcmpl-test", result.Response.ResponseId);
        Assert.Equal(ChatFinishReason.Stop, result.Response.FinishReason);
        Assert.Same(result.Usage, result.Response.AdditionalProperties![DeepSeekChatMetadata.UsageKey]);
    }

    [Fact]
    public async Task SummaryClient_EnablesThinkingAndUsesConfiguredModel()
    {
        var handler = new RecordingHandler(SuccessJson);
        var client = new DeepSeekChatClient(CreateOptions(), "test-key", DeepSeekClientRole.Summary, httpMessageHandler: handler);

        await client.GetResponseWithUsageAsync([new ChatMessage(ChatRole.User, "请总结")]);

        using var request = JsonDocument.Parse(handler.LastBody!);
        // 一个 Model 通吃：摘要角色与执行角色用同一个模型名。
        Assert.Equal("deepseek-exec", request.RootElement.GetProperty("model").GetString());
        Assert.Equal("enabled", request.RootElement.GetProperty("thinking").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ChatOptionsReasoning_OverridesExecutionThinkingDefault()
    {
        var handler = new RecordingHandler(SuccessJson);
        var client = new DeepSeekChatClient(CreateOptions(), "test-key", DeepSeekClientRole.Execution, httpMessageHandler: handler);

        await client.GetResponseWithUsageAsync(
            [new ChatMessage(ChatRole.User, "请检查")],
            new ChatOptions { Reasoning = new ReasoningOptions { Output = ReasoningOutput.Full } });

        using var request = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("enabled", request.RootElement.GetProperty("thinking").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ReasoningContent_IsEchoedOnlyWhenEnabled()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "第一轮问题"),
            new(ChatRole.Assistant,
            [
                new TextContent("第一轮结论"),
                new TextReasoningContent("第一轮推理"),
            ]),
            new(ChatRole.User, "第二轮问题"),
        };

        var echoHandler = new RecordingHandler(SuccessJson);
        var echoClient = new DeepSeekChatClient(
            CreateOptions(), "test-key", DeepSeekClientRole.Execution, echoReasoningContent: true, httpMessageHandler: echoHandler);
        await echoClient.GetResponseWithUsageAsync(history);

        using var echoRequest = JsonDocument.Parse(echoHandler.LastBody!);
        var echoedMessages = echoRequest.RootElement.GetProperty("messages");
        Assert.Equal("第一轮推理", echoedMessages[1].GetProperty("reasoning_content").GetString());

        var noEchoHandler = new RecordingHandler(SuccessJson);
        var noEchoClient = new DeepSeekChatClient(
            CreateOptions(), "test-key", DeepSeekClientRole.Execution, echoReasoningContent: false, httpMessageHandler: noEchoHandler);
        await noEchoClient.GetResponseWithUsageAsync(history);

        using var noEchoRequest = JsonDocument.Parse(noEchoHandler.LastBody!);
        Assert.All(
            noEchoRequest.RootElement.GetProperty("messages").EnumerateArray(),
            message => Assert.False(message.TryGetProperty("reasoning_content", out _)));
    }

    [Fact]
    public async Task ToolDefinitionsAndToolCalls_AreSerializedAndParsed()
    {
        const string toolCallJson = """
            {
              "id": "chatcmpl-tool",
              "model": "deepseek-chat",
              "choices": [
                {
                  "index": 0,
                  "message": {
                    "role": "assistant",
                    "content": null,
                    "tool_calls": [
                      {
                        "id": "call_1",
                        "type": "function",
                        "function": {
                          "name": "QueryMetric",
                          "arguments": "{\"metric\":\"http_p99_latency_ms\"}"
                        }
                      }
                    ]
                  },
                  "finish_reason": "tool_calls"
                }
              ],
              "usage": { "prompt_tokens": 3, "completion_tokens": 4, "total_tokens": 7 }
            }
            """;
        var handler = new RecordingHandler(toolCallJson);
        var client = new DeepSeekChatClient(CreateOptions(), "test-key", httpMessageHandler: handler);
        var echo = AIFunctionFactory.Create((string text) => text, "Echo", "回显文本");

        var result = await client.GetResponseWithUsageAsync(
            [new ChatMessage(ChatRole.User, "调用工具")],
            new ChatOptions { Tools = [echo] });

        using var request = JsonDocument.Parse(handler.LastBody!);
        var tools = request.RootElement.GetProperty("tools");
        Assert.Equal(1, tools.GetArrayLength());
        Assert.Equal("Echo", tools[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("object", tools[0].GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
        Assert.Equal("auto", request.RootElement.GetProperty("tool_choice").GetString());

        var functionCall = Assert.Single(result.Response.Messages[0].Contents.OfType<FunctionCallContent>());
        Assert.Equal("call_1", functionCall.CallId);
        Assert.Equal("QueryMetric", functionCall.Name);
        Assert.Equal("http_p99_latency_ms", functionCall.Arguments!["metric"]?.ToString());
        Assert.Equal(ChatFinishReason.ToolCalls, result.Response.FinishReason);
    }

    [Fact]
    public async Task RateLimitedResponse_ThrowsIdentifiableException()
    {
        var handler = new RecordingHandler(
            """{"error":{"message":"rate limited","type":"rate_limit"}}""",
            HttpStatusCode.TooManyRequests);
        var client = new DeepSeekChatClient(CreateOptions(), "test-key", httpMessageHandler: handler);

        var exception = await Assert.ThrowsAsync<DeepSeekRequestException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Equal(DeepSeekFailureKind.RateLimited, exception.Kind);
        Assert.Equal(429, exception.StatusCode);
    }

    [Fact]
    public async Task ServerError_ThrowsHttpError()
    {
        var handler = new RecordingHandler("""{"error":{"message":"boom"}}""", HttpStatusCode.InternalServerError);
        var client = new DeepSeekChatClient(CreateOptions(), "test-key", httpMessageHandler: handler);

        var exception = await Assert.ThrowsAsync<DeepSeekRequestException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Equal(DeepSeekFailureKind.HttpError, exception.Kind);
        Assert.Equal(500, exception.StatusCode);
    }

    [Fact]
    public async Task Timeout_ThrowsTimeoutException()
    {
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return JsonResponse(SuccessJson);
        });
        var client = new DeepSeekChatClient(CreateOptions(timeoutSeconds: 1), "test-key", httpMessageHandler: handler);

        var exception = await Assert.ThrowsAsync<DeepSeekRequestException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Equal(DeepSeekFailureKind.Timeout, exception.Kind);
    }

    [Fact]
    public void MissingApiKey_ThrowsIdentifiableException()
    {
        var exception = Assert.Throws<DeepSeekRequestException>(
            () => DeepSeekChatClientFactory.CreateClient(CreateOptions(), " "));

        Assert.Equal(DeepSeekFailureKind.MissingApiKey, exception.Kind);
        Assert.DoesNotContain("test-key", exception.Message);
    }

    [Fact]
    public async Task MissingApiKey_RegistrationFallsBackToUnavailableClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<DeepSeekOptions>();
        services.AddSingleton<ILlmSettingsStore>(
            new FixedLlmSettingsStore(new LlmSettingsSnapshot(null, null, null, null)));
        services.AddGrowthInspectorAgents(new DeepSeekOptions());
        await using var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IChatClient>();

        var exception = await Assert.ThrowsAsync<DeepSeekRequestException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Equal(DeepSeekFailureKind.MissingApiKey, exception.Kind);
        Assert.Contains("设置", exception.Message);

        var clients = scope.ServiceProvider.GetRequiredService<DeepSeekChatClients>();
        Assert.IsType<LlmChatClient>(clients.Execution);
        // 执行与摘要是两个独立实例，各自按 scope 解析配置。
        Assert.NotSame(clients.Execution, clients.Summary);
    }

    [Fact]
    public void DeepSeekOptions_BindsFromConfigurationSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DeepSeek:BaseUrl"] = "https://example.test/v1",
                ["DeepSeek:Model"] = "deepseek-chat",
                ["DeepSeek:TimeoutSeconds"] = "45",
                ["DeepSeek:ThinkingForExecution"] = "false",
                ["DeepSeek:ThinkingForSummary"] = "true",
            })
            .Build();

        var options = configuration.GetSection(DeepSeekOptions.SectionName).Get<DeepSeekOptions>();

        Assert.NotNull(options);
        Assert.Equal("https://example.test/v1", options!.BaseUrl);
        Assert.Equal("deepseek-chat", options.Model);
        Assert.Equal(45, options.TimeoutSeconds);
        Assert.False(options.ThinkingForExecution);
        Assert.True(options.ThinkingForSummary);
    }

    private static DeepSeekOptions CreateOptions(int timeoutSeconds = 10)
        => new()
        {
            BaseUrl = "https://api.deepseek.com",
            Model = "deepseek-exec",
            ThinkingForExecution = false,
            ThinkingForSummary = true,
            TimeoutSeconds = timeoutSeconds,
        };

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    /// <summary>记录请求体与 Authorization 头的假 HTTP 处理器。</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder;

        public RecordingHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            responder = (_, _) => Task.FromResult(JsonResponse(responseBody, statusCode));
        }

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            this.responder = responder;
        }

        public string? LastBody { get; private set; }

        public string? LastAuthorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            LastAuthorization = request.Headers.Authorization?.ToString();
            return await responder(request, cancellationToken);
        }
    }
}
