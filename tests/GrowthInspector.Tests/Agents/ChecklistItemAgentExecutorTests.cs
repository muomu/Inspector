using System.Text.Json;
using GrowthInspector.Agents.Chat;
using GrowthInspector.Agents.Execution;
using GrowthInspector.Agents.Tools;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Core.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Tests.Agents;

/// <summary>TPG-2709：执行 agent 的一致性、不一致、信息型与兜底路径。</summary>
public sealed class ChecklistItemAgentExecutorTests : IDisposable
{
    private readonly TestSqliteDatabase database = new();

    [Fact]
    public async Task ConsistentOk_ProducesOkAndRecomputesValue()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var chat = new ScriptedChatClient()
            .Enqueue(ToolCall())
            .Enqueue(ScriptedChatClient.Text(ClaimWith(seed.Item.Id, "Ok", "123")));

        var executor = await CreateJudgementExecutorAsync(chat, seed);
        var result = await executor.ExecuteAsync(CreateRequest(seed, judgement: true));

        Assert.Equal(CheckResultStatus.Ok, result.Status);
        Assert.Equal(VerdictStatus.Ok, result.AgentVerdict);
        Assert.Equal(FrameworkVerdictStatus.Ok, result.FrameworkVerdict);
        Assert.Equal("123", result.RecomputedValue);
        Assert.Equal(1, result.ToolCallCount);
        Assert.Null(result.ErrorMessage);
        Assert.Contains("p99", result.Evidence);

        await using var db = await database.Factory.CreateDbContextAsync();
        var log = Assert.Single(await db.ToolCallLogs.ToListAsync());
        Assert.Equal("mock__QueryMetric", log.ToolName);
        Assert.Equal(AgentRole.Execution, log.AgentRole);
        Assert.True(log.Succeeded);
    }

    [Fact]
    public async Task ConsistentAbnormal_ProducesAbnormal()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var chat = new ScriptedChatClient()
            .Enqueue(ToolCall())
            .Enqueue(ScriptedChatClient.Text(ClaimWith(seed.Item.Id, "Abnormal", "900")));

        var executor = await CreateJudgementExecutorAsync(chat, seed, """{"value":900}""");
        var result = await executor.ExecuteAsync(CreateRequest(seed, judgement: true));

        Assert.Equal(CheckResultStatus.Abnormal, result.Status);
        Assert.Equal(FrameworkVerdictStatus.Abnormal, result.FrameworkVerdict);
        Assert.Equal("900", result.RecomputedValue);
    }

    [Fact]
    public async Task AgentAndFrameworkDisagree_ProducesMismatchError()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var chat = new ScriptedChatClient()
            .Enqueue(ToolCall())
            .Enqueue(ScriptedChatClient.Text(ClaimWith(seed.Item.Id, "Abnormal", "123")));

        var executor = await CreateJudgementExecutorAsync(chat, seed, """{"value":123}""");
        var result = await executor.ExecuteAsync(CreateRequest(seed, judgement: true));

        Assert.Equal(CheckResultStatus.Error, result.Status);
        Assert.Equal(FrameworkVerdictStatus.Mismatch, result.FrameworkVerdict);
        Assert.Contains("不一致", result.ErrorMessage);
    }

    [Fact]
    public async Task InformationalItem_ReturnsInsufficientEvidence()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var chat = new ScriptedChatClient()
            .Enqueue(ScriptedChatClient.Text(ClaimWith(
                seed.Item.Id,
                "InsufficientEvidence",
                null,
                toolName: string.Empty)));

        var executor = await CreateJudgementExecutorAsync(chat, seed);
        var result = await executor.ExecuteAsync(CreateRequest(seed, judgement: false));

        Assert.Equal(CheckResultStatus.InsufficientEvidence, result.Status);
        Assert.Equal(VerdictStatus.InsufficientEvidence, result.AgentVerdict);
        Assert.Equal(FrameworkVerdictStatus.InsufficientEvidence, result.FrameworkVerdict);
        Assert.Null(result.RecomputedValue);
    }

    [Fact]
    public async Task InvalidClaimJson_ProducesErrorWithoutFabricatedVerdict()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var chat = new ScriptedChatClient()
            .Enqueue(ScriptedChatClient.Text("这是一段没有 JSON 的说明。"));

        var executor = await CreateJudgementExecutorAsync(chat, seed);
        var result = await executor.ExecuteAsync(CreateRequest(seed, judgement: true));

        Assert.Equal(CheckResultStatus.Error, result.Status);
        Assert.Null(result.AgentVerdict);
        Assert.Contains("未完成 LLM 判定", result.Narrative);
    }

    [Fact]
    public async Task LlmException_ProducesErrorWithExplicitNarrative()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var chat = new ScriptedChatClient
        {
            Handler = (_, _, _) => throw new InvalidOperationException("模型服务不可用"),
        };

        var executor = await CreateJudgementExecutorAsync(chat, seed);
        var result = await executor.ExecuteAsync(CreateRequest(seed, judgement: true));

        Assert.Equal(CheckResultStatus.Error, result.Status);
        Assert.Null(result.AgentVerdict);
        Assert.Null(result.FrameworkVerdict);
        Assert.Contains("未完成 LLM 判定", result.Narrative);
        Assert.Contains("模型服务不可用", result.ErrorMessage);
    }

    [Fact]
    public async Task MissingApiKeyClient_ProducesErrorInsteadOfFakeConclusion()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var factory = AgentTestData.CreateToolsetFactory(database);
        var executor = new ChecklistItemAgentExecutor(
            new UnavailableChatClient("未配置环境变量 DEEPSEEK_API_KEY，模型调用不可用。"),
            factory,
            database.Factory,
            Options.Create(new InspectionOptions()),
            NullLogger<ChecklistItemAgentExecutor>.Instance);

        var result = await executor.ExecuteAsync(CreateRequest(seed, judgement: true));

        Assert.Equal(CheckResultStatus.Error, result.Status);
        Assert.Contains("未完成 LLM 判定", result.Narrative);
        Assert.Null(result.AgentVerdict);
    }

    [Fact]
    public async Task Timeout_ProducesErrorWithExplicitNarrative()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var chat = new ScriptedChatClient
        {
            Handler = async (_, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return ScriptedChatClient.Text("{}");
            },
        };

        var executor = await CreateJudgementExecutorAsync(
            chat,
            seed,
            options: new InspectionOptions { AgentItemTimeoutSeconds = 1 });
        var result = await executor.ExecuteAsync(CreateRequest(seed, judgement: true));

        Assert.Equal(CheckResultStatus.Error, result.Status);
        Assert.Contains("未完成 LLM 判定", result.Narrative);
        Assert.Contains("超时", result.Narrative);
    }

    [Fact]
    public async Task MissingValuePath_ProducesFrameworkError()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var chat = new ScriptedChatClient()
            .Enqueue(ToolCall())
            .Enqueue(ScriptedChatClient.Text(ClaimWith(seed.Item.Id, "Ok", "123")));

        var executor = await CreateJudgementExecutorAsync(chat, seed, """{"other":123}""");
        var result = await executor.ExecuteAsync(CreateRequest(seed, judgement: true));

        Assert.Equal(CheckResultStatus.Error, result.Status);
        Assert.Equal(FrameworkVerdictStatus.Error, result.FrameworkVerdict);
        Assert.Contains("取值路径", result.ErrorMessage);
    }

    [Fact]
    public async Task TruncatedToolResult_ProducesFrameworkError()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var chat = new ScriptedChatClient()
            .Enqueue(ToolCall())
            .Enqueue(ScriptedChatClient.Text(ClaimWith(seed.Item.Id, "Ok", "123")));

        var executor = await CreateJudgementExecutorAsync(chat, seed, truncated: true);
        var result = await executor.ExecuteAsync(CreateRequest(seed, judgement: true));

        Assert.Equal(CheckResultStatus.Error, result.Status);
        Assert.Equal(FrameworkVerdictStatus.Error, result.FrameworkVerdict);
        Assert.Contains("截断", result.ErrorMessage);
    }

    private async Task<ChecklistItemAgentExecutor> CreateJudgementExecutorAsync(
        ScriptedChatClient chat,
        AgentSeed seed,
        string toolResult = """{"value":123}""",
        InspectionOptions? options = null,
        bool truncated = false)
    {
        var server = await AgentTestData.AddMcpServerAsync(database, seed.Project.Id, "mock");
        var invoker = new FakeMcpToolInvoker();
        invoker.ToolLists[server.Id] =
        [
            new McpToolDescriptor(
                "QueryMetric",
                "查询指标",
                """{"type":"object","properties":{"metric":{"type":"string"}}}"""),
        ];
        invoker.OnCall = (_, _, _, _) => new McpToolCallResult(true, toolResult, truncated, 3);

        var effectiveOptions = options ?? new InspectionOptions();
        var factory = AgentTestData.CreateToolsetFactory(database, invoker, options: effectiveOptions);

        return new ChecklistItemAgentExecutor(
            chat,
            factory,
            database.Factory,
            Options.Create(effectiveOptions),
            NullLogger<ChecklistItemAgentExecutor>.Instance);
    }

    private static ChatResponse ToolCall() => ScriptedChatClient.FunctionCalls(new FunctionCallContent(
        "call-1",
        "mock__QueryMetric",
        new Dictionary<string, object?> { ["metric"] = "p99" }));

    private static ChecklistItemExecutionRequest CreateRequest(AgentSeed seed, bool judgement)
    {
        var item = new ChecklistSnapshotItem(
            seed.Item.Id,
            judgement ? "接口延迟检查" : "服务信息收集",
            0,
            judgement ? "mock__QueryMetric" : null,
            judgement ? """{"metric":"http_p99_latency_ms"}""" : null,
            judgement ? "$.value" : null,
            judgement ? ComparisonOperator.Lt : null,
            judgement ? "500" : null,
            judgement ? "1h" : null,
            judgement ? Severity.Critical : null,
            1,
            judgement ? "指标服务" : null,
            judgement ? "p99 延迟应低于 500ms。" : "收集服务基本信息。");

        return new ChecklistItemExecutionRequest(
            seed.Run.Id,
            seed.Project.Id,
            seed.Environment.Id,
            "上线中，重点看延迟。",
            item,
            [new RunbookSnapshot(Guid.NewGuid(), "通用手册", RunbookKind.General, "# 通用手册\n\n指标语义。")],
            [],
            []);
    }

    private static string ClaimWith(
        Guid itemId,
        string verdict,
        string? value,
        string? toolName = "mock__QueryMetric")
        => JsonSerializer.Serialize(new
        {
            checklistItemId = itemId,
            toolName,
            value,
            evidence = "mock 返回 p99 延迟指标。",
            verdict,
            narrative = "按工具返回结果给出判定。",
            observations = new[] { "观察项一" },
        });

    public void Dispose() => database.Dispose();
}
