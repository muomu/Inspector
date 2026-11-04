using System.Text.Json;
using System.Text.RegularExpressions;
using GrowthInspector.Agents;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Core.Options;
using GrowthInspector.Data;
using GrowthInspector.Notifications;
using GrowthInspector.Orchestration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Environment = GrowthInspector.Core.Models.Environment;

namespace GrowthInspector.Tests;

/// <summary>
/// TPG-2717 收口：只使用 mock MCP server（真实 stdio 子进程）跑通一条完整检查单：
/// 快照 → 执行 agent 多轮取证 → 框架复核双判 → finding → Lark mock 留痕 → AI 摘要。
/// LLM 用脚本化 IChatClient 代替（v1 不接真实 DeepSeek key），其余全走生产代码。
/// </summary>
public sealed class EndToEndMockTests
{
    [Fact]
    public async Task MockMcpServer_EndToEnd_ProducesVerdictsFindingNotificationAndSummary()
    {
        await using var host = await E2EHost.CreateAsync(new ScriptedChatClient());
        var seed = await host.SeedProjectAsync("growth", "Growth");

        var runId = await host.RunAsync(seed.ChecklistId, seed.EnvironmentId);

        await using var db = await host.DbFactory.CreateDbContextAsync();
        var run = await db.ChecklistRuns.Include(x => x.Results).SingleAsync(x => x.Id == runId);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(3, run.Results.Count);

        var ok = run.Results.Single(x => x.ChecklistItemId == seed.HealthItemId);
        Assert.Equal(CheckResultStatus.Ok, ok.Status);
        Assert.Equal(VerdictStatus.Ok, ok.AgentVerdict);
        Assert.Equal(FrameworkVerdictStatus.Ok, ok.FrameworkVerdict);
        Assert.Equal("healthy", ok.RecomputedValue);

        var abnormal = run.Results.Single(x => x.ChecklistItemId == seed.MetricItemId);
        Assert.Equal(CheckResultStatus.Abnormal, abnormal.Status);
        Assert.Equal(VerdictStatus.Abnormal, abnormal.AgentVerdict);
        Assert.Equal(FrameworkVerdictStatus.Abnormal, abnormal.FrameworkVerdict);
        Assert.Equal("321.5", abnormal.RecomputedValue);

        var info = run.Results.Single(x => x.ChecklistItemId == seed.InfoItemId);
        Assert.Equal(CheckResultStatus.InsufficientEvidence, info.Status);

        // 工具审计来自真实 mock MCP server，且每个检查项只有一条（无重复审计）。
        var logs = await db.ToolCallLogs.Where(x => x.ChecklistRunId == runId).ToListAsync();
        var healthLog = Assert.Single(logs, x => x.ToolName == "mock__GetServiceHealth");
        Assert.Contains("healthy", healthLog.Result);
        Assert.Equal("mock", healthLog.ServerName);
        Assert.Equal(seed.HealthItemId, healthLog.ChecklistItemId);
        var metricLog = Assert.Single(logs, x => x.ToolName == "mock__QueryMetric");
        Assert.Contains("321.5", metricLog.Result);
        Assert.Equal(seed.MetricItemId, metricLog.ChecklistItemId);
        Assert.Equal(2, logs.Count);

        // 只有「agent 与框架一致判 Abnormal」的判定型检查项产生 finding 与 mock 通知。
        var finding = await db.Findings.SingleAsync(x => x.ProjectId == seed.ProjectId);
        Assert.Equal(FindingStatus.Open, finding.Status);
        Assert.Equal(1, finding.RuleVersion);
        Assert.Equal(seed.MetricItemId, finding.ChecklistItemId);

        var notification = await db.LarkNotifications.SingleAsync(x => x.ProjectId == seed.ProjectId);
        Assert.Equal(LarkNotificationKind.FindingOpened, notification.Kind);
        Assert.True(notification.IsMock);
        Assert.Contains("http p99", notification.PayloadJson);
        Assert.Contains("FindingOpened", notification.PayloadJson);

        var summary = await db.AiSummaries.SingleAsync(x => x.ChecklistRunId == runId);
        Assert.Equal(AiSummaryKind.RunSummary, summary.Kind);
        Assert.False(summary.FallbackUsed);
        Assert.Contains("端到端 mock", summary.Content);
    }

    [Fact]
    public async Task SecondProject_OnlyAddsData_RunsWithoutCodeChanges()
    {
        await using var host = await E2EHost.CreateAsync(new ScriptedChatClient());
        var first = await host.SeedProjectAsync("growth", "Growth");
        var second = await host.SeedProjectAsync("second", "第二接入方");

        var secondRunId = await host.RunAsync(second.ChecklistId, second.EnvironmentId);

        await using var db = await host.DbFactory.CreateDbContextAsync();
        var run = await db.ChecklistRuns.Include(x => x.Results).SingleAsync(x => x.Id == secondRunId);
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(3, run.Results.Count);
        Assert.Equal(1, await db.Findings.CountAsync(x => x.ProjectId == second.ProjectId));
        Assert.Equal(0, await db.Findings.CountAsync(x => x.ProjectId == first.ProjectId));

        // 第二项目沿用同一份代码，只换了 Project / Environment / McpServer / 检查单数据。
        var logs = await db.ToolCallLogs.Where(x => x.ChecklistRunId == secondRunId).ToListAsync();
        Assert.Equal(2, logs.Count);
    }

    private sealed record SeedIds(
        Guid ProjectId,
        Guid EnvironmentId,
        Guid ChecklistId,
        Guid HealthItemId,
        Guid MetricItemId,
        Guid InfoItemId);

    /// <summary>脚本化 LLM：首轮发起工具调用，拿到工具结果后输出结构化 claim；摘要请求直接返回文本。</summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            var system = list.FirstOrDefault(x => x.Role == ChatRole.System)?.Text ?? string.Empty;
            var user = list.LastOrDefault(x => x.Role == ChatRole.User)?.Text ?? string.Empty;
            var last = list[^1];

            if (system.Contains("执行摘要助手"))
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    "## 本轮摘要\n\n端到端 mock 执行完成：2 条判定型断言一致（1 Ok / 1 Abnormal），1 条信息型观察。")));
            }

            var itemId = Regex.Match(user, @"- Id：([0-9a-fA-F-]{36})").Groups[1].Value;
            var toolName = Regex.Match(user, @"- 工具名：(\S+)").Groups[1].Value;
            var hasToolResult = last.Role == ChatRole.Tool
                || last.Contents.OfType<FunctionResultContent>().Any();

            if (hasToolResult)
            {
                var verdict = toolName.EndsWith("QueryMetric", StringComparison.Ordinal) ? "Abnormal" : "Ok";
                var value = toolName.EndsWith("QueryMetric", StringComparison.Ordinal) ? "321.5" : "healthy";
                return Task.FromResult(TextResponse(ClaimJson(itemId, toolName, value, verdict)));
            }

            if (user.Contains("本项为信息型检查项", StringComparison.Ordinal))
            {
                return Task.FromResult(TextResponse(ClaimJson(
                    itemId,
                    string.Empty,
                    string.Empty,
                    "InsufficientEvidence")));
            }

            var argumentsJson = Regex.Match(user, @"- 参数模板：(\{.+\})").Groups[1].Value;
            var arguments = string.IsNullOrWhiteSpace(argumentsJson)
                ? new Dictionary<string, object?>()
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson);
            var response = new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                new List<AIContent>
                {
                    new FunctionCallContent(Guid.NewGuid().ToString("N"), toolName, arguments),
                }));
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("端到端测试脚本不支持流式响应。");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private static ChatResponse TextResponse(string text)
            => new(new ChatMessage(ChatRole.Assistant, text));

        private static string ClaimJson(string itemId, string toolName, string value, string verdict)
            => JsonSerializer.Serialize(new
            {
                checklistItemId = itemId,
                toolName,
                value,
                evidence = $"来自 mock MCP server 的工具返回：{toolName} -> {value}",
                verdict,
                narrative = verdict == "Abnormal" ? "取值为 321.5，未满足阈值 1。" : "证据与断言一致。",
                observations = Array.Empty<string>(),
            });
    }

    private sealed class E2EHost : IAsyncDisposable
    {
        private static readonly string MockServerDll = ResolveMockServerDll();

        private readonly string databasePath;

        private E2EHost(ServiceProvider services, string databasePath)
        {
            Services = services;
            this.databasePath = databasePath;
        }

        public ServiceProvider Services { get; }

        public IDbContextFactory<GrowthInspectorDbContext> DbFactory =>
            Services.GetRequiredService<IDbContextFactory<GrowthInspectorDbContext>>();

        public static async Task<E2EHost> CreateAsync(IChatClient chatClient)
        {
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                $"growth-inspector-e2e-{Guid.NewGuid():N}.db");

            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            services.Configure<InspectionOptions>(options =>
            {
                options.TimeZone = "UTC";
                options.AgentMaxTurns = 4;
                options.AgentItemTimeoutSeconds = 60;
                options.ToolResultMaxBytes = 8192;
                options.SchedulerTickSeconds = 30;
            });
            services.Configure<DeepSeekOptions>(options => options.TimeoutSeconds = 20);

            services.AddGrowthInspectorData(databasePath);
            services.AddGrowthInspectorAgents(new DeepSeekOptions());
            // 覆盖 Agents 注册的 IChatClient（v1 不接真实 DeepSeek key）。
            services.AddSingleton(chatClient);
            services.AddGrowthInspectorOrchestration();
            services.AddGrowthInspectorNotifications();

            var provider = services.BuildServiceProvider();
            await provider.EnsureGrowthInspectorDatabaseAsync();

            return new E2EHost(provider, databasePath);
        }

        public async Task<SeedIds> SeedProjectAsync(string code, string name)
        {
            await using var db = await DbFactory.CreateDbContextAsync();

            var project = new Project { Name = name, Code = code, OpsContext = "端到端测试项目上下文。" };
            var environment = new Environment { Name = "prod", Project = project, IsDefault = true };
            project.Environments.Add(environment);

            var server = new McpServer
            {
                Project = project,
                Name = "mock",
                Transport = McpTransport.Stdio,
                Command = "dotnet",
                ArgumentsJson = JsonSerializer.Serialize(new[] { MockServerDll }),
                IsEnabled = true,
                TimeoutSeconds = 60,
                MaxResultBytes = 8192,
            };

            var runbook = new Runbook
            {
                Project = project,
                Title = "通用手册",
                Kind = RunbookKind.General,
                Body = "# 通用手册\n\n- 端到端测试环境，数据源为 mock MCP server。",
            };

            var checklist = new Checklist
            {
                Project = project,
                Name = $"{name} 端到端检查单",
                Description = "TPG-2717 端到端验收用检查单",
                Frequency = ChecklistFrequency.Manual,
            };

            var health = new ChecklistItem
            {
                Title = "management-svc 进程心跳",
                SortOrder = 0,
                ToolName = "mock__GetServiceHealth",
                ArgumentsTemplate = """{"service":"management-svc"}""",
                ValuePath = "$.status",
                Operator = ComparisonOperator.Eq,
                Threshold = "healthy",
                Window = "5m",
                Severity = Severity.Critical,
                RuleVersion = 1,
                Expectation = "服务健康时返回 healthy。",
            };

            var metric = new ChecklistItem
            {
                Title = "http p99 延迟",
                SortOrder = 1,
                ToolName = "mock__QueryMetric",
                ArgumentsTemplate = """{"metric":"http_p99_latency_ms","window":"1h"}""",
                ValuePath = "$.value",
                Operator = ComparisonOperator.Lte,
                Threshold = "1",
                Window = "1h",
                Severity = Severity.Critical,
                RuleVersion = 1,
                Expectation = "阈值故意设为 1，mock 返回 321.5，用于验证异常 → finding 链路。",
            };

            var info = new ChecklistItem
            {
                Title = "信息型 观察项",
                SortOrder = 2,
                DataSourceHint = "mock__GetServiceHealth",
                Severity = Severity.Info,
                RuleVersion = 1,
            };

            checklist.Items.Add(health);
            checklist.Items.Add(metric);
            checklist.Items.Add(info);

            db.Projects.Add(project);
            db.Runbooks.Add(runbook);
            db.McpServers.Add(server);
            db.Checklists.Add(checklist);
            await db.SaveChangesAsync();

            return new SeedIds(
                project.Id,
                environment.Id,
                checklist.Id,
                health.Id,
                metric.Id,
                info.Id);
        }

        public async Task<Guid> RunAsync(Guid checklistId, Guid environmentId)
        {
            using var scope = Services.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IInspectionOrchestrator>();
            return await orchestrator.RunAsync(checklistId, environmentId);
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }

        private static string ResolveMockServerDll()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine(
                    directory.FullName,
                    "tools",
                    "GrowthInspector.MockMcpServer",
                    "bin",
                    "Debug",
                    "net10.0",
                    "GrowthInspector.MockMcpServer.dll");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException(
                "找不到 mock MCP server 构建产物，请先执行 dotnet build GrowthInspector.slnx。");
        }
    }
}
