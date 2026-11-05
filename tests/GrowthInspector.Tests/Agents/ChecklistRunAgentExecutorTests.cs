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

namespace GrowthInspector.Tests.Agents;

/// <summary>
/// 检查单执行 agent（整轮一个会话）：claim 三态映射、逐项推进与单项失败隔离、
/// 工具调用归因、缓存 token 贯穿与 LLM 故障兜底。
/// </summary>
public sealed class ChecklistRunAgentExecutorTests : IDisposable
{
    private readonly TestSqliteDatabase database = new();

    [Fact]
    public async Task AgentVerdictOk_MapsToCheckResultOk_AndAttributesToolCallToItem()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var chat = new ScriptedChatClient()
            .Enqueue(ToolCall())
            .Enqueue(ScriptedChatClient.Text(ClaimWith(seed.Item.Id, "Ok")));

        var executor = await CreateExecutorAsync(chat, seed);
        var results = await RunSingleItemAsync(executor, seed);
        var result = Assert.Single(results.Values);

        Assert.Equal(CheckResultStatus.Ok, result.Status);
        Assert.Equal(VerdictStatus.Ok, result.AgentVerdict);
        Assert.Equal(1, result.ToolCallCount);
        Assert.Null(result.ErrorMessage);
        Assert.Contains("p99", result.Evidence);
        Assert.Equal("按工具返回结果给出判定。", result.Narrative);
        Assert.Single(result.Observations!);

        await using var db = await database.Factory.CreateDbContextAsync();
        var log = Assert.Single(await db.ToolCallLogs.ToListAsync());
        Assert.Equal("mock__QueryMetric", log.ToolName);
        Assert.Equal(AgentRole.Execution, log.AgentRole);
        Assert.Equal(seed.Item.Id, log.ChecklistItemId);
        Assert.True(log.Succeeded);
    }

    [Fact]
    public async Task AgentVerdictAbnormal_MapsToCheckResultAbnormal()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var chat = new ScriptedChatClient()
            .Enqueue(ToolCall())
            .Enqueue(ScriptedChatClient.Text(ClaimWith(seed.Item.Id, "Abnormal")));

        var executor = await CreateExecutorAsync(chat, seed, """{"value":900}""");
        var results = await RunSingleItemAsync(executor, seed);
        var result = Assert.Single(results.Values);

        Assert.Equal(CheckResultStatus.Abnormal, result.Status);
        Assert.Equal(VerdictStatus.Abnormal, result.AgentVerdict);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task AgentVerdictInsufficientEvidence_MapsToInsufficientEvidence()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var chat = new ScriptedChatClient()
            .Enqueue(ScriptedChatClient.Text(ClaimWith(
                seed.Item.Id,
                "InsufficientEvidence",
                evidence: null)));

        var executor = await CreateExecutorAsync(chat, seed);
        var results = await RunSingleItemAsync(executor, seed);
        var result = Assert.Single(results.Values);

        Assert.Equal(CheckResultStatus.InsufficientEvidence, result.Status);
        Assert.Equal(VerdictStatus.InsufficientEvidence, result.AgentVerdict);
        Assert.Null(result.Evidence);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task CachedPromptTokens_ArePassedThroughPerItem()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var response = new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            ClaimWith(seed.Item.Id, "Ok")))
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 2, CachedInputTokenCount = 6 },
        };
        var chat = new ScriptedChatClient().Enqueue(response);

        var executor = await CreateExecutorAsync(chat, seed);
        var results = await RunSingleItemAsync(executor, seed);
        var result = Assert.Single(results.Values);

        Assert.Equal(10, result.PromptTokens);
        Assert.Equal(2, result.CompletionTokens);
        Assert.Equal(6, result.CachedPromptTokens);
    }

    [Fact]
    public async Task InvalidClaimOnFirstItem_MarksErrorAndSessionContinuesToNextItem()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var secondItemId = Guid.NewGuid();
        var chat = new ScriptedChatClient()
            .Enqueue(ScriptedChatClient.Text("这是一段没有 JSON 的说明。"))
            .Enqueue(ScriptedChatClient.Text(ClaimWith(secondItemId, "Ok")));

        var executor = await CreateExecutorAsync(chat, seed);
        var results = await RunTwoItemsAsync(executor, seed, secondItemId);

        Assert.Equal(CheckResultStatus.Error, results[seed.Item.Id].Status);
        Assert.Contains("未完成 LLM 判定", results[seed.Item.Id].Narrative);
        Assert.Equal(CheckResultStatus.Ok, results[secondItemId].Status);
    }

    [Fact]
    public async Task ClaimWithForeignChecklistItemId_ProducesError()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var chat = new ScriptedChatClient()
            .Enqueue(ScriptedChatClient.Text(ClaimWith(Guid.NewGuid(), "Ok")));

        var executor = await CreateExecutorAsync(chat, seed);
        var results = await RunSingleItemAsync(executor, seed);
        var result = Assert.Single(results.Values);

        Assert.Equal(CheckResultStatus.Error, result.Status);
        Assert.Null(result.AgentVerdict);
        Assert.Contains("不一致", result.ErrorMessage);
    }

    [Fact]
    public async Task LlmExceptionOnEveryItem_MarksAllItemsErrorWithoutFabricating()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var secondItemId = Guid.NewGuid();
        var chat = new ScriptedChatClient
        {
            Handler = (_, _, _) => throw new InvalidOperationException("模型服务不可用"),
        };

        var executor = await CreateExecutorAsync(chat, seed);
        var results = await RunTwoItemsAsync(executor, seed, secondItemId);

        foreach (var result in results.Values)
        {
            Assert.Equal(CheckResultStatus.Error, result.Status);
            Assert.Null(result.AgentVerdict);
            Assert.Contains("未完成 LLM 判定", result.Narrative);
            Assert.Contains("模型服务不可用", result.ErrorMessage);
        }
    }

    [Fact]
    public async Task MissingApiKeyClient_ProducesErrorInsteadOfFakeConclusion()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var factory = AgentTestData.CreateToolsetFactory(database);
        var executor = new ChecklistRunAgentExecutor(
            new UnavailableChatClient("未配置环境变量 DEEPSEEK_API_KEY，模型调用不可用。"),
            factory,
            NullLogger<ChecklistRunAgentExecutor>.Instance);

        var results = await RunSingleItemAsync(executor, seed);
        var result = Assert.Single(results.Values);

        Assert.Equal(CheckResultStatus.Error, result.Status);
        Assert.Contains("未完成 LLM 判定", result.Narrative);
        Assert.Null(result.AgentVerdict);
    }

    [Fact]
    public async Task SessionIntro_HasSharedPrefix_ItemPromptCarriesBodyOnlyForCurrentItem()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var currentBody = "当前检查项正文：p99 延迟低于 500ms 算健康；超过先看下游超时与连接池。";
        var otherBody = "另一项正文：错误率低于 1% 算健康。";
        var otherItemId = Guid.NewGuid();

        var chat = new ScriptedChatClient()
            .Enqueue(ScriptedChatClient.Text(ClaimWith(seed.Item.Id, "InsufficientEvidence", evidence: null)))
            .Enqueue(ScriptedChatClient.Text(ClaimWith(otherItemId, "Ok")));

        var executor = await CreateExecutorAsync(chat, seed);
        var snapshot = BuildSnapshot(seed, currentBody, otherItemId, otherBody);
        await ExecuteAsync(executor, BuildRequest(seed, snapshot));

        // 第一次请求 = system + 会话开场（共享前缀）+ 第 1 项推进消息。
        var firstRequest = chat.Requests[0];
        var introText = firstRequest[1].Text;
        Assert.Contains("## 通用运维手册（项目级，自动注入）", introText);
        Assert.Contains("延迟域运行健康", introText);
        Assert.Contains("错误率依赖连通", introText);
        Assert.DoesNotContain(currentBody, introText);
        Assert.DoesNotContain(otherBody, introText);
        Assert.Contains("取证纪律", firstRequest[0].Text);

        var firstItemPrompt = firstRequest[^1].Text;
        Assert.Contains(currentBody, firstItemPrompt);
        Assert.Contains(seed.Item.Id.ToString(), firstItemPrompt);
        Assert.DoesNotContain(otherBody, firstItemPrompt);

        // 第二个检查项的正文只出现在后续请求里，开场内容保持不变（前缀稳定）。
        var secondRequest = chat.Requests[^1];
        Assert.Equal(introText, secondRequest[1].Text);
        Assert.Contains(otherBody, secondRequest[^1].Text);

        // 收敛纪律与 legacy 断言字段不出现在任何提示词里。
        var allText = string.Join("\n", chat.Requests.SelectMany(messages => messages.Select(message => message.Text)));
        Assert.DoesNotContain("ValuePath", allText);
        Assert.DoesNotContain("ArgumentsTemplate", allText);
        Assert.DoesNotContain("Operator", allText);
        Assert.DoesNotContain("toolName", allText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("结构化断言", allText);
        Assert.DoesNotContain("规则版本", allText);
        Assert.DoesNotContain("数据源提示", allText);
        Assert.DoesNotContain("预期说明", allText);
    }

    private static async Task<Dictionary<Guid, ChecklistItemExecutionResult>> ExecuteAsync(
        ChecklistRunAgentExecutor executor,
        ChecklistRunAgentRequest request)
    {
        var results = new Dictionary<Guid, ChecklistItemExecutionResult>();
        await executor.ExecuteAsync(
            request,
            (itemId, result) =>
            {
                results[itemId] = result;
                return Task.CompletedTask;
            });
        return results;
    }

    private static async Task<Dictionary<Guid, ChecklistItemExecutionResult>> RunSingleItemAsync(
        ChecklistRunAgentExecutor executor,
        AgentSeed seed)
    {
        var snapshot = BuildSnapshot(
            seed,
            "检查 p99 延迟是否低于 500ms，超过则先看下游超时与连接池。");
        return await ExecuteAsync(executor, BuildRequest(seed, snapshot));
    }

    private static async Task<Dictionary<Guid, ChecklistItemExecutionResult>> RunTwoItemsAsync(
        ChecklistRunAgentExecutor executor,
        AgentSeed seed,
        Guid secondItemId)
    {
        var snapshot = BuildSnapshot(
            seed,
            "检查 p99 延迟是否低于 500ms，超过则先看下游超时与连接池。",
            secondItemId,
            "另一项正文：错误率低于 1% 算健康。");
        return await ExecuteAsync(executor, BuildRequest(seed, snapshot));
    }

    private static ChecklistSnapshot BuildSnapshot(
        AgentSeed seed,
        string currentBody,
        Guid? secondItemId = null,
        string? secondBody = null)
    {
        var items = new List<ChecklistSnapshotItem>
        {
            new(seed.Item.Id, "延迟域运行健康", 0, currentBody, Severity.Critical),
        };

        if (secondItemId is { } id)
        {
            items.Add(new ChecklistSnapshotItem(id, "错误率依赖连通", 1, secondBody ?? string.Empty, Severity.Warning));
        }

        return new ChecklistSnapshot(
            seed.Checklist.Id,
            seed.Checklist.Name,
            ChecklistFrequency.Manual,
            ChecklistTrigger.Manual,
            items,
            [new RunbookSnapshot(Guid.NewGuid(), "通用手册", RunbookKind.General, "# 通用手册\n\n指标语义。")],
            [],
            []);
    }

    private static ChecklistRunAgentRequest BuildRequest(AgentSeed seed, ChecklistSnapshot snapshot)
        => new(
            seed.Run.Id,
            seed.Project.Id,
            seed.Environment.Id,
            "上线中，重点看延迟。",
            snapshot);

    private async Task<ChecklistRunAgentExecutor> CreateExecutorAsync(
        ScriptedChatClient chat,
        AgentSeed seed,
        string toolResult = """{"value":123}""",
        InspectionOptions? options = null)
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
        invoker.OnCall = (_, _, _, _) => new McpToolCallResult(true, toolResult, false, 3);

        var factory = AgentTestData.CreateToolsetFactory(database, invoker, options: options);

        return new ChecklistRunAgentExecutor(
            chat,
            factory,
            NullLogger<ChecklistRunAgentExecutor>.Instance);
    }

    private static ChatResponse ToolCall() => ScriptedChatClient.FunctionCalls(new FunctionCallContent(
        "call-1",
        "mock__QueryMetric",
        new Dictionary<string, object?> { ["metric"] = "p99" }));

    private static string ClaimWith(
        Guid itemId,
        string verdict,
        string? evidence = "mock 返回 p99 延迟指标。")
        => JsonSerializer.Serialize(new
        {
            checklistItemId = itemId,
            evidence,
            verdict,
            narrative = "按工具返回结果给出判定。",
            observations = new[] { "观察项一" },
        });

    public void Dispose() => database.Dispose();
}
