using System.Text.Json;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Notifications.Lark;
using GrowthInspector.Notifications.Reports;
using GrowthInspector.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GrowthInspector.Tests.Notifications;

/// <summary>TPG-2714：每日 10:00 日报的内容聚合、触发时刻、Project 隔离与发送降级。</summary>
public sealed class DailyReportTests
{
    private static readonly DateOnly ReportDate = new(2026, 11, 4);

    [Fact]
    public async Task BuildContent_ContainsAllSectionsAndCounters()
    {
        await using var harness = new NotificationTestHarness(
            configureServices: services => services.AddScoped<DailyReportGenerator>());
        var seed = await harness.SeedAsync(projectName: "内容项目");
        var secondItem = await harness.AddItemAsync(seed, "错误率", sortOrder: 1, severity: Severity.Warning);

        var findingTime = new DateTimeOffset(2026, 11, 4, 2, 0, 0, TimeSpan.Zero);
        await harness.AddFindingAsync(
            seed,
            seed.Prod.Id,
            seed.Item.Id,
            FindingStatus.Open,
            findingTime,
            title: "新增：P99 延迟异常");
        await harness.AddFindingAsync(
            seed,
            seed.Prod.Id,
            secondItem.Id,
            FindingStatus.Open,
            findingTime.AddDays(-1),
            title: "持续：错误率异常");
        await harness.AddFindingAsync(
            seed,
            seed.Prod.Id,
            secondItem.Id,
            FindingStatus.Resolved,
            findingTime.AddDays(-2),
            resolvedAt: findingTime,
            closeReason: FindingCloseReason.Recovered,
            title: "恢复：错误率恢复");
        await harness.AddFindingAsync(
            seed,
            seed.Prod.Id,
            secondItem.Id,
            FindingStatus.Resolved,
            findingTime.AddDays(-3),
            resolvedAt: findingTime,
            closeReason: FindingCloseReason.RuleChanged,
            title: "变更：错误率判据调整");

        await harness.AddRunAsync(
            seed,
            seed.Prod.Id,
            [
                new CheckResultSeed(
                    seed.Item.Id,
                    CheckResultStatus.Error,
                    VerdictStatus.Ok,
                    FrameworkVerdictStatus.Mismatch,
                    "value=900",
                    ErrorMessage: "LLM 超时，复核不一致"),
                new CheckResultSeed(
                    secondItem.Id,
                    CheckResultStatus.Ok,
                    VerdictStatus.Ok,
                    FrameworkVerdictStatus.Ok,
                    "value=0.1",
                    Observations: ["连接池使用率 85%", "存在 3 条慢查询"]),
            ],
            summary: "本轮共 2 项，1 项复核不一致。");
        await harness.AddRunAsync(seed, seed.Prod.Id, [], status: RunStatus.Failed);
        await harness.AddRunAsync(seed, seed.Prod.Id, [], status: RunStatus.Skipped);
        await harness.AddRunAsync(seed, seed.Prod.Id, [], status: RunStatus.Canceled);

        using var scope = harness.Services.CreateScope();
        var builder = scope.ServiceProvider.GetRequiredService<IProjectDailyReportBuilder>();
        var report = await builder.BuildAsync(seed.Project.Id, ReportDate);

        Assert.True(report.HasData);
        Assert.Equal(4, report.Counters.Runs);
        Assert.Equal(1, report.Counters.Succeeded);
        Assert.Equal(1, report.Counters.Failed);
        Assert.Equal(1, report.Counters.Skipped);
        Assert.Equal(1, report.Counters.Canceled);
        Assert.Equal(1, report.Counters.NewFindings);
        Assert.Equal(1, report.Counters.RecoveredFindings);
        Assert.Equal(1, report.Counters.OngoingFindings);
        Assert.Equal(1, report.Counters.RuleChangedFindings);
        Assert.Equal(1, report.Counters.FailedItems);
        Assert.Equal(2, report.Counters.Observations);

        foreach (var heading in new[]
                 {
                     "## 概览", "## 新增异常", "## 恢复异常", "## 持续异常", "## 判据变更",
                     "## 失败检查项", "## 规则外观察", "## 执行异常", "## 当日 AI 摘要",
                 })
        {
            Assert.Contains(heading, report.Markdown);
        }

        Assert.Contains("新增：P99 延迟异常", report.Markdown);
        Assert.Contains("持续：错误率异常", report.Markdown);
        Assert.Contains("恢复：错误率恢复", report.Markdown);
        Assert.Contains("变更：错误率判据调整", report.Markdown);
        Assert.Contains("LLM 超时，复核不一致", report.Markdown);
        Assert.Contains("框架复核：Mismatch", report.Markdown);
        Assert.Contains("连接池使用率 85%", report.Markdown);
        Assert.Contains("本轮共 2 项，1 项复核不一致。", report.Markdown);
        Assert.Contains("跳过：", report.Markdown);
        Assert.Contains("取消：", report.Markdown);
        Assert.Contains("规则版本 1 → 1", report.Markdown);

        // Web 聚合生成器使用同一份 Project 内容，Core 接口不变。
        var generator = scope.ServiceProvider.GetRequiredService<DailyReportGenerator>();
        var aggregated = await generator.BuildAsync(ReportDate);
        Assert.Contains("# 巡检日报 2026-11-04", aggregated);
        Assert.Contains("## 内容项目 巡检日报 2026-11-04", aggregated);
        Assert.Contains("新增：P99 延迟异常", aggregated);
    }

    [Fact]
    public async Task Scheduler_TriggersOnceAtConfiguredLocalTime()
    {
        await using var harness = new NotificationTestHarness(configureInspection: options =>
        {
            options.TimeZone = "UTC";
            options.DailyScheduleTime = "10:00";
        });
        var seed = await harness.SeedAsync();
        await harness.AddRunAsync(seed, seed.Prod.Id, [CheckResultSeed.Healthy(seed.Item.Id)]);

        using var scope = harness.Services.CreateScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<DailyReportScheduler>();

        harness.Clock.UtcNow = new DateTimeOffset(2026, 11, 4, 9, 59, 0, TimeSpan.Zero);
        Assert.False(await scheduler.TickAsync());
        Assert.Empty(await harness.GetNotificationsAsync());

        harness.Clock.UtcNow = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
        Assert.True(await scheduler.TickAsync());
        var notification = Assert.Single(await harness.GetNotificationsAsync());
        Assert.Equal(LarkNotificationKind.DailyReport, notification.Kind);
        Assert.Null(notification.ChecklistRunId);
        Assert.Null(notification.FindingId);
        Assert.True(notification.IsMock);
        Assert.True(notification.Succeeded);
        Assert.Empty(harness.LarkNotifier.Sent);

        // 日报内容同时落 AiSummary(DailyReport)，供日报页读取。
        await using (var db = await harness.DbFactory.CreateDbContextAsync())
        {
            var summary = await db.AiSummaries
                .SingleAsync(x => x.Kind == AiSummaryKind.DailyReport);
            Assert.Equal(new DateOnly(2026, 11, 4), summary.ReportDate);
            Assert.Contains("# 巡检日报 2026-11-04", summary.Content);
            Assert.Contains("## ", summary.Content);
        }

        // 同日再次 tick 不重复发送。
        harness.Clock.UtcNow = new DateTimeOffset(2026, 11, 4, 10, 5, 0, TimeSpan.Zero);
        Assert.False(await scheduler.TickAsync());
        Assert.Single(await harness.GetNotificationsAsync());

        // 次日到点重新触发；次日无数据时默认不发，但当日仍记为已处理。
        harness.Clock.UtcNow = new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
        Assert.True(await scheduler.TickAsync());
        Assert.Single(await harness.GetNotificationsAsync());

        using var payload = JsonDocument.Parse(notification.PayloadJson);
        Assert.Equal("DailyReport", payload.RootElement.GetProperty("kind").GetString());
        Assert.Equal("2026-11-04", payload.RootElement.GetProperty("reportDate").GetString());
        Assert.Equal(seed.Project.Id, payload.RootElement.GetProperty("projectId").GetGuid());
    }

    [Fact]
    public async Task Projects_AreIsolated_AndUseTheirOwnWebhooks()
    {
        await using var harness = new NotificationTestHarness(configureLark: options => options.MockMode = false);
        var projectA = await harness.SeedAsync(projectName: "项目甲");
        var projectB = await harness.SeedAsync(projectName: "项目乙");
        await harness.AddRunAsync(projectA, projectA.Prod.Id, [CheckResultSeed.Healthy(projectA.Item.Id)]);
        await harness.AddRunAsync(projectB, projectB.Prod.Id, [CheckResultSeed.Healthy(projectB.Item.Id)]);

        var time = new DateTimeOffset(2026, 11, 4, 2, 0, 0, TimeSpan.Zero);
        await harness.AddFindingAsync(
            projectA, projectA.Prod.Id, projectA.Item.Id, FindingStatus.Open, time, title: "甲的异常");
        await harness.AddFindingAsync(
            projectB, projectB.Prod.Id, projectB.Item.Id, FindingStatus.Open, time, title: "乙的异常");

        // 写入各自项目 webhook（SeedAsync 后可更新）。
        await harness.QueryAsync(async db =>
        {
            var a = await db.Projects.FindAsync(projectA.Project.Id);
            var b = await db.Projects.FindAsync(projectB.Project.Id);
            a!.LarkWebhookUrl = "https://lark.invalid/hook-a";
            b!.LarkWebhookUrl = "https://lark.invalid/hook-b";
            return await db.SaveChangesAsync();
        });

        using var scope = harness.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<DailyReportRunner>();
        var results = await runner.RunAsync(ReportDate);

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(result.Sent));

        var notifications = await harness.GetNotificationsAsync();
        Assert.Equal(2, notifications.Count);
        var notificationA = Assert.Single(notifications, x => x.ProjectId == projectA.Project.Id);
        var notificationB = Assert.Single(notifications, x => x.ProjectId == projectB.Project.Id);

        using var payloadA = JsonDocument.Parse(notificationA.PayloadJson);
        using var payloadB = JsonDocument.Parse(notificationB.PayloadJson);
        var markdownA = payloadA.RootElement.GetProperty("markdown").GetString()!;
        var markdownB = payloadB.RootElement.GetProperty("markdown").GetString()!;
        Assert.Contains("甲的异常", markdownA);
        Assert.DoesNotContain("乙的异常", markdownA);
        Assert.Contains("乙的异常", markdownB);
        Assert.DoesNotContain("甲的异常", markdownB);

        Assert.Equal(2, harness.LarkNotifier.Sent.Count);
        Assert.Contains(harness.LarkNotifier.Sent, x => x.WebhookUrl == "https://lark.invalid/hook-a" && x.Markdown.Contains("甲的异常"));
        Assert.Contains(harness.LarkNotifier.Sent, x => x.WebhookUrl == "https://lark.invalid/hook-b" && x.Markdown.Contains("乙的异常"));
    }

    [Fact]
    public async Task EmptyProject_IsNotSentByDefault()
    {
        await using var harness = new NotificationTestHarness();
        var emptyProject = await harness.SeedAsync(projectName: "空项目");

        using var scope = harness.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<DailyReportRunner>();
        var results = await runner.RunAsync(ReportDate);

        var result = Assert.Single(results);
        Assert.Equal(emptyProject.Project.Id, result.ProjectId);
        Assert.False(result.Sent);
        Assert.True(result.SkippedEmpty);
        Assert.Empty(await harness.GetNotificationsAsync());
    }

    [Fact]
    public async Task EmptyProject_IsSentWhenConfigured()
    {
        await using var harness = new NotificationTestHarness(
            configureLark: options => options.SendEmptyDailyReports = true);
        var emptyProject = await harness.SeedAsync(projectName: "空项目");

        using var scope = harness.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<DailyReportRunner>();
        var results = await runner.RunAsync(ReportDate);

        Assert.True(Assert.Single(results).Sent);
        var notification = Assert.Single(await harness.GetNotificationsAsync());
        Assert.Equal(emptyProject.Project.Id, notification.ProjectId);
        Assert.Equal(LarkNotificationKind.DailyReport, notification.Kind);
        Assert.True(notification.IsMock);
        using var payload = JsonDocument.Parse(notification.PayloadJson);
        Assert.False(payload.RootElement.GetProperty("hasData").GetBoolean());
        Assert.Contains("（无）", payload.RootElement.GetProperty("markdown").GetString());
    }

    [Fact]
    public async Task RealModeSendFailure_RecordsFailedNotificationWithoutThrowing()
    {
        await using var harness = new NotificationTestHarness(configureLark: options => options.MockMode = false);
        var seed = await harness.SeedAsync(webhookUrl: "https://lark.invalid/hook");
        await harness.AddRunAsync(seed, seed.Prod.Id, [CheckResultSeed.Healthy(seed.Item.Id)]);
        harness.LarkNotifier.ThrowOnSend = true;

        using var scope = harness.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<DailyReportRunner>();
        var results = await runner.RunAsync(ReportDate);

        var result = Assert.Single(results);
        Assert.True(result.Sent);
        Assert.False(result.Succeeded);
        Assert.False(result.IsMock);

        var notification = Assert.Single(await harness.GetNotificationsAsync());
        Assert.False(notification.Succeeded);
        Assert.False(notification.IsMock);
        Assert.Contains("fake Lark 通道不可用", notification.ErrorMessage);
    }

    [Fact]
    public async Task RealModeWithoutWebhook_FallsBackToMockRecord()
    {
        await using var harness = new NotificationTestHarness(configureLark: options => options.MockMode = false);
        var seed = await harness.SeedAsync(webhookUrl: null);
        await harness.AddRunAsync(seed, seed.Prod.Id, [CheckResultSeed.Healthy(seed.Item.Id)]);

        using var scope = harness.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<DailyReportRunner>();
        var results = await runner.RunAsync(ReportDate);

        Assert.True(Assert.Single(results).Sent);
        var notification = Assert.Single(await harness.GetNotificationsAsync());
        Assert.True(notification.IsMock);
        Assert.True(notification.Succeeded);
        Assert.Empty(harness.LarkNotifier.Sent);
    }

    [Fact]
    public async Task DailyReportHostedService_IsRegistered_AndScheduleParserFallsBackToTen()
    {
        await using var harness = new NotificationTestHarness();

        Assert.Contains(
            harness.Services.GetServices<IHostedService>(),
            service => service is DailyReportHostedService);

        Assert.Equal(new TimeOnly(9, 30), DailyReportScheduler.ParseDailyScheduleTime("09:30"));
        Assert.Equal(new TimeOnly(10, 0), DailyReportScheduler.ParseDailyScheduleTime("not-a-time"));
        Assert.Equal(new TimeOnly(10, 0), DailyReportScheduler.ParseDailyScheduleTime(null));
    }
}
