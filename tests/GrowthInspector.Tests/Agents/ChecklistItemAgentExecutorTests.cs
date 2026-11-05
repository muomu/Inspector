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

/// <summary>执行 agent：claim 三态映射、正文注入与 LLM 故障兜底（不再有框架复核）。</summary>
public sealed class ChecklistItemAgentExecutorTests : IDisposable
{
    private readonly TestSqliteDatabase database = new();

    [Fact]
    public async Task AgentVerdictOk_MapsToCheckResultOk()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var chat = new ScriptedChatClient()
            .Enqueue(ToolCall())
            .Enqueue(ScriptedChatClient.Text(ClaimWith(seed.Item.Id, "Ok")));

        var executor = await CreateExecutorAsync(chat, seed);
        var result = await executor.ExecuteAsync(CreateRequest(seed));

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
        var result = await executor.ExecuteAsync(CreateRequest(seed));

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
        var result = await executor.ExecuteAsync(CreateRequest(seed));

        Assert.Equal(CheckResultStatus.InsufficientEvidence, result.Status);
        Assert.Equal(VerdictStatus.InsufficientEvidence, result.AgentVerdict);
        Assert.Null(result.Evidence);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task InvalidClaimJson_ProducesErrorWithoutFabricatedVerdict()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var chat = new ScriptedChatClient()
            .Enqueue(ScriptedChatClient.Text("这是一段没有 JSON 的说明。"));

        var executor = await CreateExecutorAsync(chat, seed);
        var result = await executor.ExecuteAsync(CreateRequest(seed));

        Assert.Equal(CheckResultStatus.Error, result.Status);
        Assert.Null(result.AgentVerdict);
        Assert.Contains("未完成 LLM 判定", result.Narrative);
    }

    [Fact]
    public async Task ClaimWithForeignChecklistItemId_ProducesError()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var chat = new ScriptedChatClient()
            .Enqueue(ScriptedChatClient.Text(ClaimWith(Guid.NewGuid(), "Ok")));

        var executor = await CreateExecutorAsync(chat, seed);
        var result = await executor.ExecuteAsync(CreateRequest(seed));

        Assert.Equal(CheckResultStatus.Error, result.Status);
        Assert.Null(result.AgentVerdict);
        Assert.Contains("不一致", result.ErrorMessage);
    }

    [Fact]
    public async Task LlmException_ProducesErrorWithExplicitNarrative()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var chat = new ScriptedChatClient
        {
            Handler = (_, _, _) => throw new InvalidOperationException("模型服务不可用"),
        };

        var executor = await CreateExecutorAsync(chat, seed);
        var result = await executor.ExecuteAsync(CreateRequest(seed));

        Assert.Equal(CheckResultStatus.Error, result.Status);
        Assert.Null(result.AgentVerdict);
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

        var result = await executor.ExecuteAsync(CreateRequest(seed));

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

        var executor = await CreateExecutorAsync(
            chat,
            seed,
            options: new InspectionOptions { AgentItemTimeoutSeconds = 1 });
        var result = await executor.ExecuteAsync(CreateRequest(seed));

        Assert.Equal(CheckResultStatus.Error, result.Status);
        Assert.Contains("未完成 LLM 判定", result.Narrative);
        Assert.Contains("超时", result.Narrative);
    }

    [Fact]
    public async Task Prompt_ContainsCurrentItemBody_AndNoLegacyAssertionFields()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var currentBody = "当前检查项正文：p99 延迟低于 500ms 算健康；超过先看下游超时与连接池。";
        var snapshot = new ChecklistSnapshot(
            seed.Checklist.Id,
            seed.Checklist.Name,
            ChecklistFrequency.Manual,
            ChecklistTrigger.Manual,
            [
                new ChecklistSnapshotItem(seed.Item.Id, "延迟域运行健康", 0, currentBody, Severity.Critical),
                new ChecklistSnapshotItem(
                    Guid.NewGuid(), "错误率依赖连通", 1, "另一项正文：错误率低于 1% 算健康。", Severity.Warning),
            ],
            [],
            [],
            []);

        await using (var db = await database.Factory.CreateDbContextAsync())
        {
            var run = await db.ChecklistRuns.SingleAsync(x => x.Id == seed.Run.Id);
            run.SnapshotJson = JsonSerializer.Serialize(
                snapshot,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await db.SaveChangesAsync();
        }

        var chat = new ScriptedChatClient()
            .Enqueue(ScriptedChatClient.Text(ClaimWith(
                seed.Item.Id,
                "InsufficientEvidence",
                evidence: null)));

        var executor = await CreateExecutorAsync(chat, seed);
        await executor.ExecuteAsync(CreateRequest(seed, currentBody));

        var messages = chat.Requests[0];
        var userText = messages.Single(message => message.Role == ChatRole.User).Text;
        var allText = string.Join("\n", messages.Select(message => message.Text));
        Assert.Contains(currentBody, userText);
        Assert.Contains("错误率依赖连通", userText);

        // system + user 提示词都不应再出现结构化断言时代的字段与措辞。
        Assert.DoesNotContain("ValuePath", allText);
        Assert.DoesNotContain("ArgumentsTemplate", allText);
        Assert.DoesNotContain("Operator", allText);
        Assert.DoesNotContain("toolName", allText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("结构化断言", allText);
        Assert.DoesNotContain("规则版本", allText);
        Assert.DoesNotContain("数据源提示", allText);
        Assert.DoesNotContain("预期说明", allText);
    }

    private async Task<ChecklistItemAgentExecutor> CreateExecutorAsync(
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

    private static ChecklistItemExecutionRequest CreateRequest(
        AgentSeed seed,
        string body = "检查 p99 延迟是否低于 500ms，超过则先看下游超时与连接池。")
    {
        var item = new ChecklistSnapshotItem(
            seed.Item.Id,
            "延迟域运行健康",
            0,
            body,
            Severity.Critical);

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
