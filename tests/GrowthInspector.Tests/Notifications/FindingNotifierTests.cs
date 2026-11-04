using System.Text.Json;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Notifications.Findings;
using GrowthInspector.Notifications.Lark;
using Microsoft.Extensions.DependencyInjection;

namespace GrowthInspector.Tests.Notifications;

/// <summary>TPG-2713：finding 去重、规则版本、恢复与 mock Lark 告警。</summary>
public sealed class FindingNotifierTests
{
    [Fact]
    public async Task ContinuousAbnormal_TwoRuns_OnlyOneOpenedNotification()
    {
        await using var harness = new NotificationTestHarness();
        var seed = await harness.SeedAsync();
        using var scope = harness.Services.CreateScope();
        var notifier = scope.ServiceProvider.GetRequiredService<IFindingNotifier>();

        var firstRun = await harness.AddRunAsync(
            seed,
            seed.Prod.Id,
            [CheckResultSeed.Abnormal(seed.Item.Id, "value=900")]);
        await notifier.EvaluateRunAsync(firstRun);

        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        var secondRun = await harness.AddRunAsync(
            seed,
            seed.Prod.Id,
            [CheckResultSeed.Abnormal(seed.Item.Id, "value=950")]);
        await notifier.EvaluateRunAsync(secondRun);

        var finding = Assert.Single(await harness.GetFindingsAsync());
        Assert.Equal(FindingStatus.Open, finding.Status);
        Assert.Equal(harness.Clock.UtcNow, finding.LastSeenAt);
        Assert.Equal("value=950", finding.Evidence);
        Assert.Equal(
            FindingNotifier.BuildDedupKey(seed.Project.Id, seed.Prod.Id, seed.Checklist.Id, seed.Item.Id),
            finding.DedupKey);

        var notification = Assert.Single(await harness.GetNotificationsAsync());
        Assert.Equal(LarkNotificationKind.FindingOpened, notification.Kind);
        Assert.Equal(finding.Id, notification.FindingId);
        Assert.True(notification.IsMock);
        Assert.True(notification.Succeeded);

        using var payload = JsonDocument.Parse(notification.PayloadJson);
        Assert.Equal(seed.Project.Id, payload.RootElement.GetProperty("projectId").GetGuid());
        Assert.Equal(seed.Prod.Id, payload.RootElement.GetProperty("environmentId").GetGuid());
        Assert.Equal(seed.Checklist.Id, payload.RootElement.GetProperty("checklistId").GetGuid());
        Assert.Equal(seed.Item.Id, payload.RootElement.GetProperty("checklistItemId").GetGuid());
        Assert.Equal("Critical", payload.RootElement.GetProperty("severity").GetString());
        Assert.Equal(1, payload.RootElement.GetProperty("ruleVersion").GetInt32());
        Assert.Contains("value=900", payload.RootElement.GetProperty("evidenceSummary").GetString());
        Assert.Empty(harness.LarkNotifier.Sent);
    }

    [Fact]
    public async Task Recovery_ResolvesFinding_AndSendsOneResolvedNotification()
    {
        await using var harness = new NotificationTestHarness();
        var seed = await harness.SeedAsync();
        using var scope = harness.Services.CreateScope();
        var notifier = scope.ServiceProvider.GetRequiredService<IFindingNotifier>();

        var abnormalRun = await harness.AddRunAsync(seed, seed.Prod.Id, [CheckResultSeed.Abnormal(seed.Item.Id)]);
        await notifier.EvaluateRunAsync(abnormalRun);

        harness.Clock.Advance(TimeSpan.FromMinutes(30));
        var healthyRun = await harness.AddRunAsync(seed, seed.Prod.Id, [CheckResultSeed.Healthy(seed.Item.Id)]);
        await notifier.EvaluateRunAsync(healthyRun);

        var finding = Assert.Single(await harness.GetFindingsAsync());
        Assert.Equal(FindingStatus.Resolved, finding.Status);
        Assert.Equal(FindingCloseReason.Recovered, finding.CloseReason);
        Assert.Equal(harness.Clock.UtcNow, finding.ResolvedAt);

        var notifications = await harness.GetNotificationsAsync();
        Assert.Equal(2, notifications.Count);
        Assert.Single(notifications, x => x.Kind == LarkNotificationKind.FindingOpened);
        var resolved = Assert.Single(notifications, x => x.Kind == LarkNotificationKind.FindingResolved);
        Assert.True(resolved.IsMock);
        Assert.True(resolved.Succeeded);

        using var payload = JsonDocument.Parse(resolved.PayloadJson);
        Assert.Equal("Recovered", payload.RootElement.GetProperty("closeReason").GetString());
        Assert.Equal(seed.Item.Id, payload.RootElement.GetProperty("checklistItemId").GetGuid());
    }

    [Fact]
    public async Task RuleVersionChange_ClosesOldFinding_AndReopensNewVersion()
    {
        await using var harness = new NotificationTestHarness();
        var seed = await harness.SeedAsync(ruleVersion: 1);
        using var scope = harness.Services.CreateScope();
        var notifier = scope.ServiceProvider.GetRequiredService<IFindingNotifier>();

        var firstRun = await harness.AddRunAsync(seed, seed.Prod.Id, [CheckResultSeed.Abnormal(seed.Item.Id)]);
        await notifier.EvaluateRunAsync(firstRun);

        await harness.UpdateItemRuleVersionAsync(seed.Item.Id, 2);
        harness.Clock.Advance(TimeSpan.FromHours(1));
        var secondRun = await harness.AddRunAsync(seed, seed.Prod.Id, [CheckResultSeed.Abnormal(seed.Item.Id, "value=901")]);
        await notifier.EvaluateRunAsync(secondRun);

        var findings = await harness.GetFindingsAsync();
        Assert.Equal(2, findings.Count);
        var oldFinding = Assert.Single(findings, x => x.RuleVersion == 1);
        Assert.Equal(FindingStatus.Resolved, oldFinding.Status);
        Assert.Equal(FindingCloseReason.RuleChanged, oldFinding.CloseReason);
        Assert.Equal(harness.Clock.UtcNow, oldFinding.ResolvedAt);

        var newFinding = Assert.Single(findings, x => x.RuleVersion == 2);
        Assert.Equal(FindingStatus.Open, newFinding.Status);
        Assert.Null(newFinding.CloseReason);
        Assert.Single(findings, x => x.Status == FindingStatus.Open);

        var notifications = await harness.GetNotificationsAsync();
        Assert.Equal(2, notifications.Count(x => x.Kind == LarkNotificationKind.FindingOpened));
        var ruleChanged = Assert.Single(notifications, x => x.Kind == LarkNotificationKind.RuleChanged);
        Assert.Equal(oldFinding.Id, ruleChanged.FindingId);
        using var payload = JsonDocument.Parse(ruleChanged.PayloadJson);
        Assert.Equal(1, payload.RootElement.GetProperty("oldRuleVersion").GetInt32());
        Assert.Equal(2, payload.RootElement.GetProperty("newRuleVersion").GetInt32());
        Assert.Equal(2, payload.RootElement.GetProperty("ruleVersion").GetInt32());
    }

    [Theory]
    [InlineData(VerdictStatus.Ok, FrameworkVerdictStatus.Abnormal)]
    [InlineData(VerdictStatus.Abnormal, FrameworkVerdictStatus.Ok)]
    [InlineData(VerdictStatus.Ok, FrameworkVerdictStatus.Mismatch)]
    [InlineData(VerdictStatus.Abnormal, FrameworkVerdictStatus.Mismatch)]
    [InlineData(VerdictStatus.Ok, FrameworkVerdictStatus.InsufficientEvidence)]
    [InlineData(null, null)]
    public async Task InconsistentVerdict_DoesNotCreateFindingOrNotification(
        VerdictStatus? agentVerdict,
        FrameworkVerdictStatus? frameworkVerdict)
    {
        await using var harness = new NotificationTestHarness();
        var seed = await harness.SeedAsync();
        using var scope = harness.Services.CreateScope();
        var notifier = scope.ServiceProvider.GetRequiredService<IFindingNotifier>();

        var result = new CheckResultSeed(
            seed.Item.Id,
            CheckResultStatus.Error,
            agentVerdict,
            frameworkVerdict,
            "value=900");
        var runId = await harness.AddRunAsync(seed, seed.Prod.Id, [result]);
        await notifier.EvaluateRunAsync(runId);

        Assert.Empty(await harness.GetFindingsAsync());
        Assert.Empty(await harness.GetNotificationsAsync());
    }

    [Fact]
    public async Task Environments_AreIsolatedByDedupKey()
    {
        await using var harness = new NotificationTestHarness();
        var seed = await harness.SeedAsync(environmentCount: 2);
        using var scope = harness.Services.CreateScope();
        var notifier = scope.ServiceProvider.GetRequiredService<IFindingNotifier>();
        var beta = seed.Environments[1];

        await notifier.EvaluateRunAsync(
            await harness.AddRunAsync(seed, seed.Prod.Id, [CheckResultSeed.Abnormal(seed.Item.Id)]));
        await notifier.EvaluateRunAsync(
            await harness.AddRunAsync(seed, beta.Id, [CheckResultSeed.Abnormal(seed.Item.Id)]));

        var openFindings = await harness.GetFindingsAsync();
        Assert.Equal(2, openFindings.Count);
        Assert.All(openFindings, finding => Assert.Equal(FindingStatus.Open, finding.Status));
        Assert.Equal(2, openFindings.Select(x => x.DedupKey).Distinct().Count());

        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await notifier.EvaluateRunAsync(
            await harness.AddRunAsync(seed, beta.Id, [CheckResultSeed.Healthy(seed.Item.Id)]));

        var findings = await harness.GetFindingsAsync();
        var prodFinding = Assert.Single(findings, x => x.EnvironmentId == seed.Prod.Id);
        Assert.Equal(FindingStatus.Open, prodFinding.Status);
        var betaFinding = Assert.Single(findings, x => x.EnvironmentId == beta.Id);
        Assert.Equal(FindingStatus.Resolved, betaFinding.Status);
        Assert.Equal(FindingCloseReason.Recovered, betaFinding.CloseReason);
        Assert.Equal(2, (await harness.GetNotificationsAsync()).Count(x => x.Kind == LarkNotificationKind.FindingOpened));
        Assert.Single(await harness.GetNotificationsAsync(), x => x.Kind == LarkNotificationKind.FindingResolved);
    }

    [Fact]
    public async Task InfoSeverity_RecordsFindingWithoutNotification()
    {
        await using var harness = new NotificationTestHarness();
        var seed = await harness.SeedAsync(severity: Severity.Info);
        using var scope = harness.Services.CreateScope();
        var notifier = scope.ServiceProvider.GetRequiredService<IFindingNotifier>();

        await notifier.EvaluateRunAsync(
            await harness.AddRunAsync(seed, seed.Prod.Id, [CheckResultSeed.Abnormal(seed.Item.Id)]));

        var finding = Assert.Single(await harness.GetFindingsAsync());
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Equal(FindingStatus.Open, finding.Status);
        Assert.Empty(await harness.GetNotificationsAsync());
    }

    [Fact]
    public async Task InformationalItem_DoesNotCreateFinding()
    {
        await using var harness = new NotificationTestHarness();
        var seed = await harness.SeedAsync(severity: null, withAssertion: false);
        using var scope = harness.Services.CreateScope();
        var notifier = scope.ServiceProvider.GetRequiredService<IFindingNotifier>();

        await notifier.EvaluateRunAsync(
            await harness.AddRunAsync(
                seed,
                seed.Prod.Id,
                [new CheckResultSeed(
                    seed.Item.Id,
                    CheckResultStatus.Ok,
                    VerdictStatus.Ok,
                    FrameworkVerdictStatus.Ok,
                    "value=120")]));

        Assert.Empty(await harness.GetFindingsAsync());
        Assert.Empty(await harness.GetNotificationsAsync());
    }

    [Fact]
    public async Task RealModeWithoutWebhook_FallsBackToMockRecord()
    {
        await using var harness = new NotificationTestHarness(configureLark: options => options.MockMode = false);
        var seed = await harness.SeedAsync(webhookUrl: null);
        using var scope = harness.Services.CreateScope();
        var notifier = scope.ServiceProvider.GetRequiredService<IFindingNotifier>();

        await notifier.EvaluateRunAsync(
            await harness.AddRunAsync(seed, seed.Prod.Id, [CheckResultSeed.Abnormal(seed.Item.Id)]));

        var notification = Assert.Single(await harness.GetNotificationsAsync());
        Assert.True(notification.IsMock);
        Assert.True(notification.Succeeded);
        Assert.Empty(harness.LarkNotifier.Sent);
    }

    [Fact]
    public async Task RealModeWithWebhook_UsesRealChannelAndRecordsIsMockFalse()
    {
        await using var harness = new NotificationTestHarness(configureLark: options => options.MockMode = false);
        const string webhook = "https://lark.invalid/hook";
        var seed = await harness.SeedAsync(webhookUrl: webhook);
        using var scope = harness.Services.CreateScope();
        var notifier = scope.ServiceProvider.GetRequiredService<IFindingNotifier>();

        await notifier.EvaluateRunAsync(
            await harness.AddRunAsync(seed, seed.Prod.Id, [CheckResultSeed.Abnormal(seed.Item.Id)]));

        var sent = Assert.Single(harness.LarkNotifier.Sent);
        Assert.Equal(webhook, sent.WebhookUrl);
        Assert.Contains("新异常", sent.Markdown);

        var notification = Assert.Single(await harness.GetNotificationsAsync());
        Assert.False(notification.IsMock);
        Assert.True(notification.Succeeded);
    }
}
