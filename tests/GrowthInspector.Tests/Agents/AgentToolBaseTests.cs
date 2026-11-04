using System.Text.Json;
using GrowthInspector.Agents.Tools;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Core.Options;
using GrowthInspector.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Tests.Agents;

/// <summary>TPG-2708：工具命名、手册检索与披露、审计、角色隔离、MCP 刷新。</summary>
public sealed class AgentToolBaseTests : IDisposable
{
    private readonly TestSqliteDatabase database = new();

    [Fact]
    public async Task ToolPrefixes_AndRegistrationConflicts()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var server = await AgentTestData.AddMcpServerAsync(database, seed.Project.Id, "mock");
        var invoker = new FakeMcpToolInvoker();
        invoker.ToolLists[server.Id] =
        [
            new McpToolDescriptor("QueryMetric", "查询指标", """{"type":"object","properties":{"metric":{"type":"string"}}}"""),
            new McpToolDescriptor("logs.query", "查询日志", null),
        ];

        var factory = AgentTestData.CreateToolsetFactory(database, invoker);
        var toolset = await factory.CreateAsync(
            new AgentToolsetRequest(seed.Project.Id, seed.Environment.Id, seed.Run.Id, seed.Item.Id, AgentRole.Execution));

        var names = toolset.Tools.Select(tool => tool.Name).ToList();
        Assert.Contains("local__search_runbooks", names);
        Assert.Contains("local__read_runbook", names);
        Assert.Contains("local__list_runbooks", names);
        Assert.Contains("local__update_runbook_section", names);
        Assert.Contains("mock__QueryMetric", names);
        Assert.Contains("mock__logs_query", names);

        var registry = new AgentToolRegistry();
        registry.AddMcpTool("mock", "QueryMetric", null, null, server.Id, (_, _) => Task.FromResult("{}"));
        Assert.Throws<InvalidOperationException>(() =>
            registry.AddMcpTool("mock", "QueryMetric", null, null, server.Id, (_, _) => Task.FromResult("{}")));

        var localRegistry = new AgentToolRegistry();
        localRegistry.AddLocal(Microsoft.Extensions.AI.AIFunctionFactory.Create(
            (string query) => query,
            name: "local__search_runbooks",
            description: "占位"));
        Assert.Throws<InvalidOperationException>(() =>
            localRegistry.AddMcpTool("local", "search_runbooks", null, null, server.Id, (_, _) => Task.FromResult("{}")));

        var added = localRegistry.TryAddMcpTool(
            "local", "search_runbooks", null, null, server.Id, (_, _) => Task.FromResult("{}"), out var error);
        Assert.False(added);
        Assert.NotNull(error);
        Assert.Single(localRegistry.RegistrationErrors);
    }

    [Fact]
    public async Task SearchRunbooks_HitsChineseSubstring_AndFiltersArchivedAndOtherProject()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var target = await AgentTestData.AddRunbookAsync(
            database,
            seed.Project.Id,
            "数据库排查",
            "# 数据库排查\n\n连接池耗尽时先看 max_connections。");

        await AgentTestData.AddRunbookAsync(
            database,
            seed.Project.Id,
            "磁盘排查",
            "# 磁盘排查\n\n磁盘空间不足时清理归档日志。");

        await AgentTestData.AddRunbookAsync(
            database,
            seed.Project.Id,
            "已归档手册",
            "# 已归档\n\n连接池历史经验。",
            archived: true);

        var otherProject = new Project { Name = "另一个项目", Code = "other-" + Guid.NewGuid().ToString("N")[..8] };
        await using (var db = await database.Factory.CreateDbContextAsync())
        {
            db.Projects.Add(otherProject);
            await db.SaveChangesAsync();
        }

        await AgentTestData.AddRunbookAsync(
            database,
            otherProject.Id,
            "别的项目手册",
            "# 别的项目\n\n连接池经验不应跨 Project。");

        var options = Options.Create(new InspectionOptions { RunbookSearchLimit = 5 });
        var service = new RunbookSearchService(
            database.Factory, options, NullLogger<RunbookSearchService>.Instance);

        var hits = await service.SearchAsync(seed.Project.Id, "连接池");
        var hit = Assert.Single(hits);
        Assert.Equal(target.Id, hit.RunbookId);
        Assert.Contains("连接池", hit.Snippet);
        Assert.True(service.FtsIndexAvailable);

        // 关闭 FTS5 后走 LIKE 兜底，命中与片段行为必须一致。
        var fallback = new RunbookSearchService(
            database.Factory, options, NullLogger<RunbookSearchService>.Instance, enableFtsIndex: false);
        var fallbackHits = await fallback.SearchAsync(seed.Project.Id, "连接池");
        Assert.Equal(hits.Select(x => (x.RunbookId, x.Title)), fallbackHits.Select(x => (x.RunbookId, x.Title)));

        // 短于 trigram 长度的中文子串也必须命中，且两条路径一致。
        var shortHits = await service.SearchAsync(seed.Project.Id, "连接");
        var shortFallback = await fallback.SearchAsync(seed.Project.Id, "连接");
        Assert.Single(shortHits);
        Assert.Equal(shortHits[0].RunbookId, Assert.Single(shortFallback).RunbookId);

        var noHit = await service.SearchAsync(seed.Project.Id, "不存在的关键词");
        Assert.Empty(noHit);
    }

    [Fact]
    public async Task SearchRunbooks_RespectsRunbookSearchLimit()
    {
        var seed = await AgentTestData.SeedAsync(database);
        for (var index = 0; index < 3; index++)
        {
            await AgentTestData.AddRunbookAsync(
                database,
                seed.Project.Id,
                $"手册{index}",
                $"# 手册{index}\n\n共同的索引关键词。");
        }

        var service = new RunbookSearchService(
            database.Factory,
            Options.Create(new InspectionOptions { RunbookSearchLimit = 2 }),
            NullLogger<RunbookSearchService>.Instance);

        var hits = await service.SearchAsync(seed.Project.Id, "索引关键词");
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public async Task ReadRunbook_SupportsSectionProgressiveDisclosure()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var body = """
            # 数据库手册

            概览内容。

            ## 连接池

            查看 max_connections。

            ### 子节

            细节说明。

            ## 磁盘

            清理日志。
            """;
        var runbook = await AgentTestData.AddRunbookAsync(database, seed.Project.Id, "数据库手册", body);

        var factory = AgentTestData.CreateToolsetFactory(database);
        var toolset = await factory.CreateAsync(
            new AgentToolsetRequest(seed.Project.Id, seed.Environment.Id, seed.Run.Id, seed.Item.Id, AgentRole.Execution));

        var full = await toolset.InvokeAsync(
            AgentToolNames.ReadRunbook,
            JsonSerializer.Serialize(new { runbookId = runbook.Id.ToString() }));
        using (var document = JsonDocument.Parse(full.Text))
        {
            Assert.True(document.RootElement.GetProperty("ok").GetBoolean(), full.Text);
            Assert.Contains("概览内容", document.RootElement.GetProperty("content").GetString());
        }

        var section = await toolset.InvokeAsync(
            AgentToolNames.ReadRunbook,
            JsonSerializer.Serialize(new { runbookId = runbook.Id.ToString(), section = "连接池" }));
        using (var document = JsonDocument.Parse(section.Text))
        {
            Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
            var content = document.RootElement.GetProperty("content").GetString()!;
            Assert.Contains("## 连接池", content);
            Assert.Contains("查看 max_connections", content);
            Assert.Contains("### 子节", content);
            Assert.DoesNotContain("清理日志", content);
        }

        var missing = await toolset.InvokeAsync(
            AgentToolNames.ReadRunbook,
            JsonSerializer.Serialize(new { runbookId = runbook.Id.ToString(), section = "不存在的章节" }));
        using (var document = JsonDocument.Parse(missing.Text))
        {
            Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains(
                "连接池",
                document.RootElement.GetProperty("availableSections").EnumerateArray().Select(x => x.GetString()!));
        }
    }

    [Fact]
    public async Task UpdateRunbookSection_ReturnsErrorsWithoutThrowing()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var runbook = await AgentTestData.AddRunbookAsync(database, seed.Project.Id, "手册", "# 手册\n\n正文。");
        var writes = new FakeRunbookWriteService();
        var factory = AgentTestData.CreateToolsetFactory(database, runbookWriteService: writes);
        var toolset = await factory.CreateAsync(
            new AgentToolsetRequest(seed.Project.Id, seed.Environment.Id, seed.Run.Id, seed.Item.Id, AgentRole.Reflection));

        var success = await toolset.InvokeAsync(
            AgentToolNames.UpdateRunbookSection,
            JsonSerializer.Serialize(new
            {
                runbookId = runbook.Id.ToString(),
                section = "排查步骤",
                content = "1. 看连接数",
            }));
        using (var document = JsonDocument.Parse(success.Text))
        {
            Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        }

        var call = Assert.Single(writes.Calls);
        Assert.Equal(runbook.Id, call.RunbookId);
        Assert.Equal("排查步骤", call.Section);
        Assert.Equal("1. 看连接数", call.Content);

        writes.OnUpdate = (_, _, _) => new RunbookSectionUpdateResult(false, "章节名称重复。");
        var failed = await toolset.InvokeAsync(
            AgentToolNames.UpdateRunbookSection,
            JsonSerializer.Serialize(new { runbookId = runbook.Id.ToString(), section = "排查步骤", content = "x" }));
        using (var document = JsonDocument.Parse(failed.Text))
        {
            Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("章节名称重复。", document.RootElement.GetProperty("error").GetString());
        }

        writes.OnUpdate = (_, _, _) => throw new InvalidOperationException("写库异常");
        var thrown = await toolset.InvokeAsync(
            AgentToolNames.UpdateRunbookSection,
            JsonSerializer.Serialize(new { runbookId = runbook.Id.ToString(), section = "排查步骤", content = "x" }));
        using (var document = JsonDocument.Parse(thrown.Text))
        {
            Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains("写库异常", document.RootElement.GetProperty("error").GetString());
        }

        var invalidId = await toolset.InvokeAsync(
            AgentToolNames.UpdateRunbookSection,
            JsonSerializer.Serialize(new { runbookId = "not-a-guid", section = "x", content = "y" }));
        using (var document = JsonDocument.Parse(invalidId.Text))
        {
            Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        }

        Guid otherProjectId;
        await using (var db = await database.Factory.CreateDbContextAsync())
        {
            var otherProject = new Project { Name = "别的项目", Code = "other-" + Guid.NewGuid().ToString("N")[..8] };
            db.Projects.Add(otherProject);
            await db.SaveChangesAsync();
            otherProjectId = otherProject.Id;
        }

        var otherProjectRunbook = await AgentTestData.AddRunbookAsync(
            database,
            otherProjectId,
            "别人的手册",
            "# 别人的手册\n\n正文。");
        var crossProject = await toolset.InvokeAsync(
            AgentToolNames.UpdateRunbookSection,
            JsonSerializer.Serialize(new
            {
                runbookId = otherProjectRunbook.Id.ToString(),
                section = "x",
                content = "y",
            }));
        using (var document = JsonDocument.Parse(crossProject.Text))
        {
            Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        }

        var noWriteServiceFactory = AgentTestData.CreateToolsetFactory(database);
        var noWriteServiceToolset = await noWriteServiceFactory.CreateAsync(
            new AgentToolsetRequest(seed.Project.Id, seed.Environment.Id, seed.Run.Id, seed.Item.Id, AgentRole.Reflection));
        var unavailable = await noWriteServiceToolset.InvokeAsync(
            AgentToolNames.UpdateRunbookSection,
            JsonSerializer.Serialize(new { runbookId = runbook.Id.ToString(), section = "x", content = "y" }));
        using (var document = JsonDocument.Parse(unavailable.Text))
        {
            Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains("未注册", document.RootElement.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task Invoke_WritesToolCallLog_ForLocalAndMcp_AndPassesContext()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var server = await AgentTestData.AddMcpServerAsync(database, seed.Project.Id, "mock");
        var invoker = new FakeMcpToolInvoker();
        invoker.ToolLists[server.Id] =
        [
            new McpToolDescriptor("QueryMetric", "查询指标", """{"type":"object","properties":{"metric":{"type":"string"}}}"""),
        ];
        invoker.OnCall = (_, _, _, _) => new McpToolCallResult(
            true,
            JsonSerializer.Serialize(new { value = 123, note = new string('长', 200) }),
            false,
            5);

        var options = new InspectionOptions { ToolResultMaxBytes = 64 };
        var factory = AgentTestData.CreateToolsetFactory(database, invoker, options: options);
        var toolset = await factory.CreateAsync(
            new AgentToolsetRequest(seed.Project.Id, seed.Environment.Id, seed.Run.Id, seed.Item.Id, AgentRole.Execution));

        await toolset.InvokeAsync(AgentToolNames.ListRunbooks, "{}");
        var mcpResult = await toolset.InvokeAsync("mock__QueryMetric", """{"metric":"p99"}""");

        Assert.Equal(2, toolset.ToolCallCount);
        var mcpCall = Assert.Single(invoker.Calls);
        Assert.Equal(server.Id, mcpCall.McpServerId);
        Assert.Equal("QueryMetric", mcpCall.ToolName);
        Assert.NotNull(mcpCall.Context);
        Assert.Equal(seed.Run.Id, mcpCall.Context!.ChecklistRunId);
        Assert.Equal(seed.Item.Id, mcpCall.Context.ChecklistItemId);
        Assert.Equal(AgentRole.Execution, mcpCall.Context.AgentRole);
        Assert.True(mcpResult.Truncated is false);

        await using var db = await database.Factory.CreateDbContextAsync();
        var logs = await db.ToolCallLogs.OrderBy(x => x.CreatedAt).ToListAsync();
        Assert.Equal(2, logs.Count);
        Assert.All(logs, log => Assert.Equal(seed.Run.Id, log.ChecklistRunId));
        Assert.All(logs, log => Assert.Equal(seed.Item.Id, log.ChecklistItemId));
        Assert.All(logs, log => Assert.Equal(AgentRole.Execution, log.AgentRole));

        var localLog = Assert.Single(logs, log => log.ToolName == AgentToolNames.ListRunbooks);
        Assert.Null(localLog.McpServerId);
        Assert.Null(localLog.ServerName);

        var mcpLog = Assert.Single(logs, log => log.ToolName == "mock__QueryMetric");
        Assert.Equal(server.Id, mcpLog.McpServerId);
        Assert.Equal("mock", mcpLog.ServerName);
        Assert.True(mcpLog.Truncated);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(mcpLog.Result!) <= 260);
    }

    [Fact]
    public async Task ToolsetFactory_IsolatesRoles_AndExcludesManagementCrud()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var server = await AgentTestData.AddMcpServerAsync(database, seed.Project.Id, "mock");
        var invoker = new FakeMcpToolInvoker();
        invoker.ToolLists[server.Id] = [new McpToolDescriptor("QueryMetric", "查询指标", null)];
        var factory = AgentTestData.CreateToolsetFactory(database, invoker);

        var execution = await factory.CreateAsync(
            new AgentToolsetRequest(seed.Project.Id, seed.Environment.Id, seed.Run.Id, seed.Item.Id, AgentRole.Execution));
        var reflection = await factory.CreateAsync(
            new AgentToolsetRequest(seed.Project.Id, seed.Environment.Id, seed.Run.Id, seed.Item.Id, AgentRole.Reflection));
        var summary = await factory.CreateAsync(
            new AgentToolsetRequest(seed.Project.Id, seed.Environment.Id, seed.Run.Id, seed.Item.Id, AgentRole.Summary));

        Assert.Empty(summary.Tools);
        Assert.Contains(execution.Tools, tool => tool.Name == "mock__QueryMetric");
        Assert.DoesNotContain(reflection.Tools, tool => tool.Name == "mock__QueryMetric");
        Assert.Contains(reflection.Tools, tool => tool.Name == AgentToolNames.UpdateRunbookSection);
        Assert.DoesNotContain(execution.Tools, tool => tool.Name.Contains("create_runbook", StringComparison.Ordinal));
        Assert.DoesNotContain(execution.Tools, tool => tool.Name.Contains("delete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefreshMcpTools_ReplacesStaleTools()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var server = await AgentTestData.AddMcpServerAsync(database, seed.Project.Id, "mock");
        var invoker = new FakeMcpToolInvoker();
        invoker.ToolLists[server.Id] = [new McpToolDescriptor("OldTool", "旧工具", null)];

        var factory = AgentTestData.CreateToolsetFactory(database, invoker);
        var toolset = await factory.CreateAsync(
            new AgentToolsetRequest(seed.Project.Id, seed.Environment.Id, seed.Run.Id, seed.Item.Id, AgentRole.Execution));
        Assert.Contains(toolset.Tools, tool => tool.Name == "mock__OldTool");

        invoker.ToolLists[server.Id] = [new McpToolDescriptor("NewTool", "新工具", null)];
        await factory.RefreshMcpToolsAsync(toolset, seed.Project.Id, seed.Environment.Id);

        Assert.DoesNotContain(toolset.Tools, tool => tool.Name == "mock__OldTool");
        Assert.Contains(toolset.Tools, tool => tool.Name == "mock__NewTool");
    }

    [Fact]
    public async Task DisabledOrArchivedMcpServers_AreNotLoaded()
    {
        var seed = await AgentTestData.SeedAsync(database);
        var enabled = await AgentTestData.AddMcpServerAsync(database, seed.Project.Id, "enabled");
        await AgentTestData.AddMcpServerAsync(database, seed.Project.Id, "disabled", enabled: false);
        await AgentTestData.AddMcpServerAsync(database, seed.Project.Id, "archived", archived: true);
        var invoker = new FakeMcpToolInvoker();
        invoker.ToolLists[enabled.Id] = [new McpToolDescriptor("Ok", "可用", null)];

        var factory = AgentTestData.CreateToolsetFactory(database, invoker);
        var toolset = await factory.CreateAsync(
            new AgentToolsetRequest(seed.Project.Id, seed.Environment.Id, seed.Run.Id, seed.Item.Id, AgentRole.Execution));

        Assert.Contains(toolset.Tools, tool => tool.Name == "enabled__Ok");
        Assert.DoesNotContain(toolset.Tools, tool => tool.Name.StartsWith("disabled__", StringComparison.Ordinal));
        Assert.DoesNotContain(toolset.Tools, tool => tool.Name.StartsWith("archived__", StringComparison.Ordinal));
    }

    public void Dispose() => database.Dispose();
}
