using System.Text.Json;
using GrowthInspector.Agents.Reflection;
using GrowthInspector.Agents.Tools;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Core.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Tests.Agents;

/// <summary>TPG-2710：反思 agent 的写工具接线、审计、非法 runbookId 与假 LLM 兜底。</summary>
public sealed class ReflectionAgentTests : IDisposable
{
    private readonly TestSqliteDatabase database = new();

    [Fact]
    public async Task Reflect_WritesRunbookSection_AndAuditsToolCall()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var runbook = await AgentTestData.AddRunbookAsync(
            database,
            seed.Project.Id,
            "接口稳定性手册",
            "# 接口稳定性手册\n\n## 延迟\n\np99 超过 500ms 时排查。");
        var writes = new FakeRunbookWriteService();

        var chat = new ScriptedChatClient()
            .Enqueue(ScriptedChatClient.FunctionCalls(new FunctionCallContent(
                "call-1",
                AgentToolNames.UpdateRunbookSection,
                new Dictionary<string, object?>
                {
                    ["runbookId"] = runbook.Id.ToString(),
                    ["section"] = "延迟",
                    ["content"] = "p99 超过 500ms 时先看下游超时与连接池。",
                })))
            .Enqueue(ScriptedChatClient.Text(JsonSerializer.Serialize(new
            {
                narrative = "补充了延迟排查步骤。",
                writes = new[]
                {
                    new
                    {
                        runbookId = runbook.Id.ToString(),
                        section = "延迟",
                        summary = "新增下游超时与连接池排查。",
                    },
                },
            })));

        var agent = CreateAgent(chat, writes);
        var outcome = await agent.ReflectAsync(
            CreateRequest(seed, runbook),
            CreateResult());

        Assert.True(outcome.Attempted);
        Assert.Null(outcome.ErrorMessage);
        Assert.Single(outcome.Writes);
        Assert.Equal(runbook.Id, outcome.Writes[0].RunbookId);
        Assert.Equal("延迟", outcome.Writes[0].Section);

        var write = Assert.Single(writes.Calls);
        Assert.Equal(runbook.Id, write.RunbookId);
        Assert.Equal("延迟", write.Section);
        Assert.Contains("连接池", write.Content);

        await using var db = await database.Factory.CreateDbContextAsync();
        var log = Assert.Single(await db.ToolCallLogs.ToListAsync());
        Assert.Equal(AgentToolNames.UpdateRunbookSection, log.ToolName);
        Assert.Equal(AgentRole.Reflection, log.AgentRole);
        Assert.Equal(seed.Run.Id, log.ChecklistRunId);
        Assert.Equal(seed.Item.Id, log.ChecklistItemId);
    }

    [Fact]
    public async Task Reflect_IllegalRunbookId_ReturnsToolErrorToModel_AndDoesNotWrite()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var runbook = await AgentTestData.AddRunbookAsync(
            database,
            seed.Project.Id,
            "接口稳定性手册",
            "# 接口稳定性手册\n\n## 延迟\n\n旧内容。");
        var writes = new FakeRunbookWriteService();

        var chat = new ScriptedChatClient()
            .Enqueue(ScriptedChatClient.FunctionCalls(new FunctionCallContent(
                "call-1",
                AgentToolNames.UpdateRunbookSection,
                new Dictionary<string, object?>
                {
                    ["runbookId"] = "not-a-guid",
                    ["section"] = "延迟",
                    ["content"] = "新内容",
                })))
            .Enqueue(ScriptedChatClient.Text("""{"narrative":"runbookId 非法，未改写。","writes":[]}"""));

        var agent = CreateAgent(chat, writes);
        var outcome = await agent.ReflectAsync(
            CreateRequest(seed, runbook),
            CreateResult());

        Assert.True(outcome.Attempted);
        Assert.Empty(outcome.Writes);
        Assert.Empty(writes.Calls);

        // 第二次请求必须带上工具返回的 {ok:false,error}，由模型自行纠正。
        var toolResults = chat.Requests[1]
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .ToList();
        var toolResult = Assert.Single(toolResults);
        Assert.Contains("\"ok\":false", toolResult.Result?.ToString());
        Assert.Contains("GUID", toolResult.Result?.ToString());
    }

    [Fact]
    public async Task Reflect_LlmFailure_ReturnsOutcomeWithoutWrites()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var runbook = await AgentTestData.AddRunbookAsync(database, seed.Project.Id, "手册", "# 手册\n\n正文。");
        var writes = new FakeRunbookWriteService();

        var chat = new ScriptedChatClient
        {
            Handler = (_, _, _) => throw new InvalidOperationException("模型不可用"),
        };

        var agent = CreateAgent(chat, writes);
        var outcome = await agent.ReflectAsync(
            CreateRequest(seed, runbook),
            CreateResult());

        Assert.False(outcome.Attempted);
        Assert.Empty(outcome.Writes);
        Assert.Empty(writes.Calls);
        Assert.Contains("模型不可用", outcome.ErrorMessage);
    }

    [Fact]
    public async Task Reflect_InvalidFinalJson_ReturnsOutcomeWithoutWrites()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var runbook = await AgentTestData.AddRunbookAsync(database, seed.Project.Id, "手册", "# 手册\n\n正文。");
        var writes = new FakeRunbookWriteService();

        var chat = new ScriptedChatClient()
            .Enqueue(ScriptedChatClient.Text("没有 JSON 的反思说明。"));

        var agent = CreateAgent(chat, writes);
        var outcome = await agent.ReflectAsync(
            CreateRequest(seed, runbook),
            CreateResult());

        Assert.False(outcome.Attempted);
        Assert.Empty(outcome.Writes);
        Assert.NotNull(outcome.ErrorMessage);
    }

    private ReflectionAgent CreateAgent(ScriptedChatClient chat, FakeRunbookWriteService writes)
    {
        var inspectionOptions = Options.Create(new InspectionOptions());
        var factory = AgentTestData.CreateToolsetFactory(
            database,
            runbookWriteService: writes,
            options: inspectionOptions.Value);

        return new ReflectionAgent(
            chat,
            factory,
            inspectionOptions,
            NullLogger<ReflectionAgent>.Instance);
    }

    private static ChecklistItemExecutionRequest CreateRequest(AgentSeed seed, Runbook runbook)
    {
        var item = new ChecklistSnapshotItem(
            seed.Item.Id,
            "接口延迟检查",
            0,
            "mock__QueryMetric",
            """{"metric":"http_p99_latency_ms"}""",
            "$.value",
            ComparisonOperator.Lt,
            "500",
            "1h",
            Severity.Critical,
            1,
            null,
            "p99 延迟应低于 500ms。");

        return new ChecklistItemExecutionRequest(
            seed.Run.Id,
            seed.Project.Id,
            seed.Environment.Id,
            "重点看延迟。",
            item,
            [],
            [new RunbookSnapshot(runbook.Id, runbook.Title, runbook.Kind, runbook.Body)],
            []);
    }

    private static ChecklistItemExecutionResult CreateResult()
        => new(
            Status: CheckResultStatus.Abnormal,
            Evidence: "p99=900ms",
            DurationMs: 1200,
            AgentVerdict: VerdictStatus.Abnormal,
            FrameworkVerdict: FrameworkVerdictStatus.Abnormal,
            RecomputedValue: "900",
            Narrative: "延迟超过阈值。",
            Observations: ["下游连接池接近上限"],
            ToolCallCount: 1);

    public void Dispose() => database.Dispose();
}
