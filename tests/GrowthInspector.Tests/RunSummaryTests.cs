using GrowthInspector.Core.Models;
using GrowthInspector.Core.Options;
using GrowthInspector.Orchestration.Summaries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Environment = GrowthInspector.Core.Models.Environment;

namespace GrowthInspector.Tests;

/// <summary>TPG-2711：摘要字段、thinking 透传、LLM 故障 / 空响应 / 无客户端时的模板兜底。</summary>
public sealed class RunSummaryTests
{
    [Fact]
    public async Task Summarize_PersistsModelFields_AndPassesAllResultsAndThinking()
    {
        var chatClient = new RecordingChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "模型摘要正文"))
            {
                Usage = new UsageDetails { InputTokenCount = 120, OutputTokenCount = 45 },
            });

        await using var host = await InspectorTestHost.CreateAsync(
            afterBuilder: services => services.AddSingleton<IChatClient>(chatClient),
            configureDeepSeek: options =>
            {
                options.SummaryModel = "summary-model";
                options.ThinkingForSummary = true;
            });

        var seeded = await SeedRunAsync(host, "summary");
        var summaryId = await host.Services
            .GetRequiredService<IRunSummaryService>()
            .SummarizeRunAsync(seeded.Run.Id);

        Assert.NotNull(summaryId);

        await using var db = await host.DbFactory.CreateDbContextAsync();
        var summary = await db.AiSummaries.SingleAsync(x => x.Id == summaryId);
        Assert.Equal(AiSummaryKind.RunSummary, summary.Kind);
        Assert.Equal(seeded.Run.Id, summary.ChecklistRunId);
        Assert.Equal("模型摘要正文", summary.Content);
        Assert.Equal("summary-model", summary.Model);
        Assert.False(summary.FallbackUsed);
        Assert.Equal(120, summary.PromptTokens);
        Assert.Equal(45, summary.CompletionTokens);
        Assert.True(summary.DurationMs >= 0);

        // thinking 透传约定：M.E.AI Reasoning + DeepSeek 兼容字段同时给出。
        Assert.NotNull(chatClient.Options);
        Assert.Equal("summary-model", chatClient.Options!.ModelId);
        Assert.NotNull(chatClient.Options.Reasoning);
        Assert.Equal(ReasoningEffort.Medium, chatClient.Options.Reasoning!.Effort);
        Assert.NotNull(chatClient.Options.AdditionalProperties);
        Assert.True(chatClient.Options.AdditionalProperties!.ContainsKey("thinking"));

        // 输入必须包含全部 CheckResult（含双判、observations、Error、耗时与 token）。
        Assert.Equal(2, chatClient.Messages.Count);
        Assert.Equal(ChatRole.System, chatClient.Messages[0].Role);
        var prompt = chatClient.Messages[1].Text;
        Assert.Contains("检查项一", prompt);
        Assert.Contains("检查项二", prompt);
        Assert.Contains("检查项三", prompt);
        Assert.Contains("Abnormal", prompt);
        Assert.Contains("Error", prompt);
        Assert.Contains("812", prompt);
        Assert.Contains("磁盘使用率正常", prompt);
        Assert.Contains("MCP 调用超时", prompt);
        Assert.Contains("token", prompt);
    }

    [Fact]
    public async Task Summarize_WhenChatClientThrows_UsesTemplateFallbackWithoutFabrication()
    {
        await using var host = await InspectorTestHost.CreateAsync(
            afterBuilder: services => services.AddSingleton<IChatClient>(
                new ThrowingChatClient(new InvalidOperationException("未配置 DEEPSEEK_API_KEY"))));

        var seeded = await SeedRunAsync(host, "summary-fallback");
        var summaryId = await host.Services
            .GetRequiredService<IRunSummaryService>()
            .SummarizeRunAsync(seeded.Run.Id);

        await using var db = await host.DbFactory.CreateDbContextAsync();
        var summary = await db.AiSummaries.SingleAsync(x => x.Id == summaryId);

        Assert.True(summary.FallbackUsed);
        Assert.Null(summary.Model);
        Assert.Equal(0, summary.PromptTokens);
        Assert.Equal(0, summary.CompletionTokens);
        Assert.Contains("模型摘要不可用", summary.Content);
        Assert.Contains("检查单-summary-fallback", summary.Content);
        Assert.Contains("Abnormal 1", summary.Content);
        Assert.Contains("Error 1", summary.Content);
        Assert.Contains("MCP 调用超时", summary.Content);
    }

    [Fact]
    public async Task Summarize_WhenThinkingDisabled_DoesNotSetReasoning()
    {
        var chatClient = new RecordingChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "摘要")));

        await using var host = await InspectorTestHost.CreateAsync(
            afterBuilder: services => services.AddSingleton<IChatClient>(chatClient),
            configureDeepSeek: options => options.ThinkingForSummary = false);

        var seeded = await SeedRunAsync(host, "no-thinking");
        await host.Services.GetRequiredService<IRunSummaryService>().SummarizeRunAsync(seeded.Run.Id);

        Assert.NotNull(chatClient.Options);
        Assert.Null(chatClient.Options!.Reasoning);
        Assert.True(chatClient.Options.AdditionalProperties is null or { Count: 0 });
    }

    [Fact]
    public async Task Summarize_WhenModelReturnsEmptyText_UsesTemplateFallback()
    {
        await using var host = await InspectorTestHost.CreateAsync(
            afterBuilder: services => services.AddSingleton<IChatClient>(
                new RecordingChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "   ")))));

        var seeded = await SeedRunAsync(host, "empty");
        var summaryId = await host.Services
            .GetRequiredService<IRunSummaryService>()
            .SummarizeRunAsync(seeded.Run.Id);

        await using var db = await host.DbFactory.CreateDbContextAsync();
        var summary = await db.AiSummaries.SingleAsync(x => x.Id == summaryId);
        Assert.True(summary.FallbackUsed);
        Assert.Contains("模型摘要不可用", summary.Content);
    }

    [Fact]
    public async Task Summarize_WhenModelCallTimesOut_UsesTemplateFallback()
    {
        await using var host = await InspectorTestHost.CreateAsync(
            afterBuilder: services => services.AddSingleton<IChatClient>(new SlowChatClient()),
            configureDeepSeek: options => options.TimeoutSeconds = 1);

        var seeded = await SeedRunAsync(host, "timeout");
        var summaryId = await host.Services
            .GetRequiredService<IRunSummaryService>()
            .SummarizeRunAsync(seeded.Run.Id);

        await using var db = await host.DbFactory.CreateDbContextAsync();
        var summary = await db.AiSummaries.SingleAsync(x => x.Id == summaryId);
        Assert.True(summary.FallbackUsed);
        Assert.Contains("模型摘要不可用", summary.Content);
    }

    [Fact]
    public async Task Summarize_WhenNoChatClientRegistered_UsesTemplateFallback()
    {
        await using var host = await InspectorTestHost.CreateAsync();
        var seeded = await SeedRunAsync(host, "no-client");

        var service = new RunSummaryService(
            host.DbFactory,
            chatClient: null,
            Options.Create(new DeepSeekOptions()),
            TimeProvider.System,
            NullLogger<RunSummaryService>.Instance);

        var summaryId = await service.SummarizeRunAsync(seeded.Run.Id);

        await using var db = await host.DbFactory.CreateDbContextAsync();
        var summary = await db.AiSummaries.SingleAsync(x => x.Id == summaryId);
        Assert.True(summary.FallbackUsed);
        Assert.Contains("模型摘要不可用", summary.Content);
    }

    private static async Task<SeededRun> SeedRunAsync(InspectorTestHost host, string suffix)
    {
        var project = new Project { Name = $"摘要项目-{suffix}", Code = $"summary-{suffix}" };
        var environment = new Environment { Name = "prod", Project = project };
        project.Environments.Add(environment);

        var checklist = new Checklist
        {
            Name = $"检查单-{suffix}",
            Frequency = ChecklistFrequency.Manual,
            Project = project,
        };

        var itemOne = new ChecklistItem { Title = "检查项一", SortOrder = 0, Severity = Severity.Warning };
        var itemTwo = new ChecklistItem { Title = "检查项二", SortOrder = 1, Severity = Severity.Critical };
        var itemThree = new ChecklistItem { Title = "检查项三", SortOrder = 2, Severity = Severity.Info };
        checklist.Items.Add(itemOne);
        checklist.Items.Add(itemTwo);
        checklist.Items.Add(itemThree);

        var startedAt = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var run = new ChecklistRun
        {
            Checklist = checklist,
            Environment = environment,
            Trigger = ChecklistTrigger.Schedule,
            Status = RunStatus.Succeeded,
            SnapshotJson = "{}",
            StartedAt = startedAt,
            CompletedAt = startedAt.AddSeconds(3),
            DurationMs = 3000,
            PromptTokens = 100,
            CompletionTokens = 50,
        };

        run.Results.Add(new CheckResult
        {
            ChecklistItem = itemOne,
            Status = CheckResultStatus.Ok,
            AgentVerdict = VerdictStatus.Ok,
            Narrative = "指标正常",
            ObservationsJson = "[\"磁盘使用率正常\"]",
            DurationMs = 12,
            PromptTokens = 3,
            CompletionTokens = 2,
            ToolCallCount = 1,
        });
        run.Results.Add(new CheckResult
        {
            ChecklistItem = itemTwo,
            Status = CheckResultStatus.Abnormal,
            AgentVerdict = VerdictStatus.Abnormal,
            Narrative = "p99 超过 500ms",
            Evidence = "{\"value\":812}",
            DurationMs = 20,
            PromptTokens = 4,
            CompletionTokens = 2,
            ToolCallCount = 2,
        });
        run.Results.Add(new CheckResult
        {
            ChecklistItem = itemThree,
            Status = CheckResultStatus.Error,
            ErrorMessage = "MCP 调用超时",
            DurationMs = 180000,
        });

        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            db.Projects.Add(project);
            db.ChecklistRuns.Add(run);
            await db.SaveChangesAsync();
        }

        return new SeededRun(run);
    }

    private sealed record SeededRun(ChecklistRun Run);

    private sealed class RecordingChatClient(ChatResponse response) : IChatClient
    {
        public List<ChatMessage> Messages { get; } = [];

        public ChatOptions? Options { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Messages.AddRange(messages);
            Options = options;
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("测试 stub 不支持流式响应。");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingChatClient(Exception exception) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw exception;

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("测试 stub 不支持流式响应。");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class SlowChatClient : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "不会返回"));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("测试 stub 不支持流式响应。");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
