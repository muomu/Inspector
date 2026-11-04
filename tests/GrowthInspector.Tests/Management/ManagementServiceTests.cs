using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Management;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrowthInspector.Tests.Management;

/// <summary>管理 service：归档过滤、凭据掩码、跨 Project 引用与名称唯一。</summary>
public sealed class ManagementServiceTests
{
    [Fact]
    public async Task Archive_HidesFromList_ButGetStillReturnsEntity()
    {
        await using var context = await ManagementTestContext.CreateAsync();
        var projectId = await context.Management.CreateProjectAsync(new CreateProjectRequest("Growth"));
        var environmentId = await context.Management.CreateEnvironmentAsync(
            new CreateEnvironmentRequest(projectId, "prod"));
        var serverId = await context.Management.CreateMcpServerAsync(
            new CreateMcpServerRequest(projectId, "metrics", Command: "node server.js"));
        var checklistId = await context.Management.CreateChecklistAsync(
            new CreateChecklistRequest(projectId, "核心链路"));

        await context.Management.SetEnvironmentArchivedAsync(environmentId, true);
        await context.Management.SetMcpServerArchivedAsync(serverId, true);
        await context.Management.SetChecklistArchivedAsync(checklistId, true);

        Assert.Empty((await context.Management.ListEnvironmentsAsync(projectId: projectId)).Items);
        Assert.Empty((await context.Management.ListMcpServersAsync(projectId: projectId)).Items);
        Assert.Empty((await context.Management.ListChecklistsAsync(projectId: projectId)).Items);

        Assert.True((await context.Management.GetEnvironmentAsync(environmentId))!.IsArchived);
        Assert.True((await context.Management.GetMcpServerAsync(serverId))!.IsArchived);
        Assert.True((await context.Management.GetChecklistAsync(checklistId))!.IsArchived);

        Assert.Single((await context.Management.ListEnvironmentsAsync(projectId: projectId, includeArchived: true)).Items);
        Assert.Single((await context.Management.ListMcpServersAsync(projectId: projectId, includeArchived: true)).Items);
        Assert.Single((await context.Management.ListChecklistsAsync(projectId: projectId, includeArchived: true)).Items);

        await context.Management.SetEnvironmentArchivedAsync(environmentId, false);
        Assert.Single((await context.Management.ListEnvironmentsAsync(projectId: projectId)).Items);
    }

    [Fact]
    public async Task Credentials_AreMaskedOnRead_AndKeptWhenMaskedOrBlankOnUpdate()
    {
        const string webhook = "https://open.larksuite.com/hook/SECRET1234";
        const string command = "node server.js --token secret";
        const string argumentsJson = "[\"--port\",\"8080\"]";
        const string url = "https://mcp.example.com/internal?token=secret";

        await using var context = await ManagementTestContext.CreateAsync();
        var projectId = await context.Management.CreateProjectAsync(
            new CreateProjectRequest("Growth", LarkWebhookUrl: webhook));
        var stdioId = await context.Management.CreateMcpServerAsync(
            new CreateMcpServerRequest(
                projectId,
                "stdio-server",
                McpTransport.Stdio,
                Command: command,
                ArgumentsJson: argumentsJson));
        var httpId = await context.Management.CreateMcpServerAsync(
            new CreateMcpServerRequest(projectId, "http-server", McpTransport.Http, Url: url));

        var project = await context.Management.GetProjectAsync(projectId);
        Assert.NotNull(project);
        Assert.NotEqual(webhook, project!.LarkWebhookUrl);
        Assert.StartsWith("****", project.LarkWebhookUrl);
        Assert.DoesNotContain("SECRET", project.LarkWebhookUrl);

        var stdio = await context.Management.GetMcpServerAsync(stdioId);
        Assert.NotNull(stdio);
        Assert.StartsWith("****", stdio!.Command);
        Assert.StartsWith("****", stdio.ArgumentsJson);
        Assert.DoesNotContain("secret", stdio.Command);

        var http = await context.Management.GetMcpServerAsync(httpId);
        Assert.NotNull(http);
        Assert.StartsWith("****", http!.Url);
        Assert.DoesNotContain("token=secret", http.Url);

        // 传掩码保持原值。
        await context.Management.UpdateProjectAsync(
            projectId,
            new UpdateProjectRequest("Growth", LarkWebhookUrl: project.LarkWebhookUrl));
        await context.Management.UpdateMcpServerAsync(
            stdioId,
            new UpdateMcpServerRequest(
                "stdio-server",
                McpTransport.Stdio,
                Command: stdio.Command,
                ArgumentsJson: stdio.ArgumentsJson));
        await context.Management.UpdateMcpServerAsync(
            httpId,
            new UpdateMcpServerRequest("http-server", McpTransport.Http, Url: http.Url));

        // 传空同样保持原值。
        await context.Management.UpdateProjectAsync(
            projectId,
            new UpdateProjectRequest("Growth", LarkWebhookUrl: ""));
        await context.Management.UpdateMcpServerAsync(
            stdioId,
            new UpdateMcpServerRequest("stdio-server", McpTransport.Stdio, Command: ""));

        await using (var db = await context.DbFactory.CreateDbContextAsync())
        {
            var storedProject = await db.Projects.SingleAsync(x => x.Id == projectId);
            Assert.Equal(webhook, storedProject.LarkWebhookUrl);

            var storedStdio = await db.McpServers.SingleAsync(x => x.Id == stdioId);
            Assert.Equal(command, storedStdio.Command);
            Assert.Equal(argumentsJson, storedStdio.ArgumentsJson);

            var storedHttp = await db.McpServers.SingleAsync(x => x.Id == httpId);
            Assert.Equal(url, storedHttp.Url);
        }

        // 传新值则替换。
        await context.Management.UpdateProjectAsync(
            projectId,
            new UpdateProjectRequest("Growth", LarkWebhookUrl: "https://open.larksuite.com/hook/NEW9999"));
        var updated = await context.Management.GetProjectAsync(projectId);
        Assert.Equal("****9999", updated!.LarkWebhookUrl);
    }

    [Fact]
    public async Task CrossProject_ReferencesAreRejected()
    {
        await using var context = await ManagementTestContext.CreateAsync();
        var projectA = await context.Management.CreateProjectAsync(new CreateProjectRequest("Alpha"));
        var projectB = await context.Management.CreateProjectAsync(new CreateProjectRequest("Beta"));
        var environmentA = await context.Management.CreateEnvironmentAsync(
            new CreateEnvironmentRequest(projectA, "prod"));
        var environmentB = await context.Management.CreateEnvironmentAsync(
            new CreateEnvironmentRequest(projectB, "prod"));
        var runbookB = await context.Management.CreateRunbookAsync(
            new CreateRunbookRequest(projectB, "Beta 手册"));
        var checklistA = await context.Management.CreateChecklistAsync(
            new CreateChecklistRequest(projectA, "Alpha 检查单"));

        var bindError = await Assert.ThrowsAsync<ManagementException>(() =>
            context.Management.CreateMcpServerAsync(new CreateMcpServerRequest(
                projectA,
                "wrong-env",
                Command: "node server.js",
                EnvironmentId: environmentB)));
        Assert.Contains("同一 Project", bindError.Message);

        var refError = await Assert.ThrowsAsync<ManagementException>(() =>
            context.Management.AddChecklistRunbookRefAsync(checklistA, runbookB));
        Assert.Contains("同一 Project", refError.Message);

        var triggerError = await Assert.ThrowsAsync<ManagementException>(() =>
            context.Management.EnqueueChecklistRunAsync(checklistA, environmentB));
        Assert.Contains("同一 Project", triggerError.Message);

        // 同 Project 的引用可以建立。
        var runbookA = await context.Management.CreateRunbookAsync(
            new CreateRunbookRequest(projectA, "Alpha 手册"));
        await context.Management.AddChecklistRunbookRefAsync(checklistA, runbookA);
        Assert.Single(await context.Management.ListChecklistRunbookRefsAsync(checklistA));

        // 触发请求落到同一项目的环境。
        var enqueued = await context.Management.EnqueueChecklistRunAsync(checklistA, environmentA);
        Assert.Equal(ChecklistTrigger.Manual, enqueued.Trigger);
    }

    [Fact]
    public async Task Names_AreUniqueWithinProject_ButReusableAcrossProjects()
    {
        await using var context = await ManagementTestContext.CreateAsync();
        var projectA = await context.Management.CreateProjectAsync(new CreateProjectRequest("Alpha"));
        var projectB = await context.Management.CreateProjectAsync(new CreateProjectRequest("Beta"));

        await context.Management.CreateEnvironmentAsync(new CreateEnvironmentRequest(projectA, "prod"));
        await context.Management.CreateEnvironmentAsync(new CreateEnvironmentRequest(projectB, "prod"));

        var environmentError = await Assert.ThrowsAsync<ManagementException>(() =>
            context.Management.CreateEnvironmentAsync(new CreateEnvironmentRequest(projectA, "prod")));
        Assert.Contains("已存在环境", environmentError.Message);

        await context.Management.CreateMcpServerAsync(
            new CreateMcpServerRequest(projectA, "metrics", Command: "node server.js"));
        await context.Management.CreateMcpServerAsync(
            new CreateMcpServerRequest(projectB, "metrics", Command: "node server.js"));

        var serverError = await Assert.ThrowsAsync<ManagementException>(() =>
            context.Management.CreateMcpServerAsync(
                new CreateMcpServerRequest(projectA, "metrics", Command: "node server.js")));
        Assert.Contains("已存在 MCP server", serverError.Message);

        await context.Management.CreateChecklistAsync(new CreateChecklistRequest(projectA, "核心链路"));
        await context.Management.CreateChecklistAsync(new CreateChecklistRequest(projectB, "核心链路"));

        var checklistError = await Assert.ThrowsAsync<ManagementException>(() =>
            context.Management.CreateChecklistAsync(new CreateChecklistRequest(projectA, "核心链路")));
        Assert.Contains("已存在检查单", checklistError.Message);

        await context.Management.CreateRunbookAsync(new CreateRunbookRequest(projectA, "手册"));
        await context.Management.CreateRunbookAsync(new CreateRunbookRequest(projectB, "手册"));

        var runbookError = await Assert.ThrowsAsync<ManagementException>(() =>
            context.Management.CreateRunbookAsync(new CreateRunbookRequest(projectA, "手册")));
        Assert.Contains("已存在手册", runbookError.Message);
    }

    [Fact]
    public async Task ChecklistItem_AssertionChange_BumpsRuleVersion_AndRejectsPartialAssertion()
    {
        await using var context = await ManagementTestContext.CreateAsync();
        var projectId = await context.Management.CreateProjectAsync(new CreateProjectRequest("Growth"));
        var checklistId = await context.Management.CreateChecklistAsync(
            new CreateChecklistRequest(projectId, "核心链路"));

        var itemId = await context.Management.CreateChecklistItemAsync(
            new CreateChecklistItemRequest(
                checklistId,
                new ChecklistItemInput(
                    "P99 延迟",
                    ToolName: "mock__QueryMetric",
                    ArgumentsTemplate: "{\"metric\":\"http_p99\"}",
                    ValuePath: "$.value",
                    Operator: ComparisonOperator.Lt,
                    Threshold: "500",
                    Window: "1h",
                    Severity: Severity.Critical)));

        var item = await context.Management.GetChecklistItemAsync(itemId);
        Assert.NotNull(item);
        Assert.True(item!.HasAssertion);
        Assert.True(item.IsJudgement);
        Assert.Equal(1, item.RuleVersion);

        // 判据未变：RuleVersion 不变。
        await context.Management.UpdateChecklistItemAsync(
            itemId,
            new UpdateChecklistItemRequest(new ChecklistItemInput(
                "P99 延迟",
                ToolName: "mock__QueryMetric",
                ArgumentsTemplate: "{\"metric\":\"http_p99\"}",
                ValuePath: "$.value",
                Operator: ComparisonOperator.Lt,
                Threshold: "500",
                Window: "1h",
                Severity: Severity.Critical)));
        Assert.Equal(1, (await context.Management.GetChecklistItemAsync(itemId))!.RuleVersion);

        // 阈值变化：RuleVersion 自动 +1。
        await context.Management.UpdateChecklistItemAsync(
            itemId,
            new UpdateChecklistItemRequest(new ChecklistItemInput(
                "P99 延迟",
                ToolName: "mock__QueryMetric",
                ArgumentsTemplate: "{\"metric\":\"http_p99\"}",
                ValuePath: "$.value",
                Operator: ComparisonOperator.Lt,
                Threshold: "800",
                Window: "1h",
                Severity: Severity.Critical)));
        Assert.Equal(2, (await context.Management.GetChecklistItemAsync(itemId))!.RuleVersion);

        // 显式给 RuleVersion 时以调用方为准。
        await context.Management.UpdateChecklistItemAsync(
            itemId,
            new UpdateChecklistItemRequest(new ChecklistItemInput(
                "P99 延迟",
                ToolName: "mock__QueryMetric",
                ArgumentsTemplate: "{\"metric\":\"http_p99\"}",
                ValuePath: "$.value",
                Operator: ComparisonOperator.Lt,
                Threshold: "900",
                Window: "1h",
                Severity: Severity.Critical,
                RuleVersion: 7)));
        Assert.Equal(7, (await context.Management.GetChecklistItemAsync(itemId))!.RuleVersion);

        // 断言字段只填一半：拒绝保存。
        var partialError = await Assert.ThrowsAsync<ManagementException>(() =>
            context.Management.CreateChecklistItemAsync(
                new CreateChecklistItemRequest(
                    checklistId,
                    new ChecklistItemInput("半截断言", ToolName: "mock__QueryMetric"))));
        Assert.Contains("同时填写", partialError.Message);

        // 参数模板必须是 JSON 对象。
        var jsonError = await Assert.ThrowsAsync<ManagementException>(() =>
            context.Management.CreateChecklistItemAsync(
                new CreateChecklistItemRequest(
                    checklistId,
                    new ChecklistItemInput(
                        "坏模板",
                        ToolName: "mock__QueryMetric",
                        ArgumentsTemplate: "not-json",
                        Operator: ComparisonOperator.Lt,
                        Threshold: "1"))));
        Assert.Contains("JSON", jsonError.Message);
    }

    [Fact]
    public async Task RunbookWriteService_UpsertsSection_AndRejectsArchivedRunbook()
    {
        await using var context = await ManagementTestContext.CreateAsync();
        var projectId = await context.Management.CreateProjectAsync(new CreateProjectRequest("Growth"));
        var runbookId = await context.Management.CreateRunbookAsync(new CreateRunbookRequest(
            projectId,
            "巡检手册",
            "# 巡检手册\n\n## 指标\n\n旧内容\n\n## 其他\n\n保留内容\n"));

        var replace = await context.RunbookWriter.UpdateSectionAsync(runbookId, "指标", "新的指标说明");
        Assert.True(replace.Ok);

        var appended = await context.RunbookWriter.UpdateSectionAsync(runbookId, "新增章节", "追加内容");
        Assert.True(appended.Ok);

        var view = await context.Management.GetRunbookAsync(runbookId);
        Assert.NotNull(view);
        Assert.Contains("新的指标说明", view!.Body);
        Assert.DoesNotContain("旧内容", view.Body);
        Assert.Contains("保留内容", view.Body);
        Assert.Contains("## 新增章节", view.Body);
        Assert.Contains("追加内容", view.Body);

        await context.Management.SetRunbookArchivedAsync(runbookId, true);
        var archived = await context.RunbookWriter.UpdateSectionAsync(runbookId, "指标", "不应写入");
        Assert.False(archived.Ok);
        Assert.Contains("归档", archived.Error);
    }

    [Fact]
    public async Task TriggerRun_OnlyEnqueues_AndRejectsArchivedChecklist()
    {
        var queue = new RecordingChecklistRunQueue();
        await using var context = await ManagementTestContext.CreateAsync(
            services => services.AddSingleton<IChecklistRunQueue>(queue));

        var projectId = await context.Management.CreateProjectAsync(new CreateProjectRequest("Growth"));
        var environmentId = await context.Management.CreateEnvironmentAsync(
            new CreateEnvironmentRequest(projectId, "prod"));
        var checklistId = await context.Management.CreateChecklistAsync(
            new CreateChecklistRequest(projectId, "核心链路"));

        var result = await context.Management.EnqueueChecklistRunAsync(checklistId, environmentId);

        Assert.Equal(ChecklistTrigger.Manual, result.Trigger);
        var enqueued = Assert.Single(queue.Enqueued);
        Assert.Equal(checklistId, enqueued.ChecklistId);
        Assert.Equal(environmentId, enqueued.EnvironmentId);

        await using (var db = await context.DbFactory.CreateDbContextAsync())
        {
            Assert.Equal(0, await db.ChecklistRuns.CountAsync());
        }

        await context.Management.SetChecklistArchivedAsync(checklistId, true);
        await Assert.ThrowsAsync<ManagementException>(() =>
            context.Management.EnqueueChecklistRunAsync(checklistId, environmentId));
        Assert.Single(queue.Enqueued);
    }
}
