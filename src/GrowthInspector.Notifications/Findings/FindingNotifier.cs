using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Core.Options;
using GrowthInspector.Data;
using GrowthInspector.Notifications.Internal;
using GrowthInspector.Notifications.Lark;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Notifications.Findings;

/// <summary>
/// finding 状态机（TPG-2713）：
/// 只有 agent 判定与框架复核一致（同 Ok 或同 Abnormal）的判定型检查项才进入状态机；
/// 按 (Project, Environment, Checklist, ChecklistItem) 去重，持续只更新、恢复 / 规则变更各发一次通知；
/// 通知统一走 LarkNotificationDispatcher，v1 为 mock 通道，只写 LarkNotification 留痕。
/// </summary>
public sealed class FindingNotifier(
    IDbContextFactory<GrowthInspectorDbContext> dbContextFactory,
    ILarkNotificationDispatcher larkDispatcher,
    IOptions<LarkNotificationOptions> larkOptions,
    IOptions<InspectionOptions> inspectionOptions,
    TimeProvider timeProvider,
    ILogger<FindingNotifier> logger) : IFindingNotifier
{
    public async Task EvaluateRunAsync(Guid checklistRunId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var run = await db.ChecklistRuns
            .AsNoTracking()
            .Include(x => x.Checklist)
            .Include(x => x.Environment)
            .Include(x => x.Results)
                .ThenInclude(x => x.ChecklistItem)
            .SingleOrDefaultAsync(x => x.Id == checklistRunId, cancellationToken)
            ?? throw new InvalidOperationException($"执行轮次 {checklistRunId} 不存在，无法进入 finding 状态机。");

        if (run.Checklist is null || run.Environment is null)
        {
            throw new InvalidOperationException($"执行轮次 {checklistRunId} 缺少检查单或环境关联，无法进入 finding 状态机。");
        }

        var project = await db.Projects
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == run.Checklist.ProjectId, cancellationToken)
            ?? throw new InvalidOperationException($"执行轮次 {checklistRunId} 所属 Project 不存在。");

        var timeZone = InspectionTimeZone.Resolve(inspectionOptions.Value.TimeZone);
        var now = timeProvider.GetUtcNow();
        var checklistName = run.Checklist.Name;
        var environmentName = run.Environment.Name;

        var orderedResults = run.Results
            .OrderBy(x => x.ChecklistItem?.SortOrder ?? int.MaxValue)
            .ThenBy(x => x.ChecklistItemId);

        foreach (var result in orderedResults)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = result.ChecklistItem;
            if (item is null)
            {
                // 检查项已删除（外键为 Restrict，正常不会出现），无法判定严重级别与规则版本。
                logger.LogDebug("执行 {RunId} 的检查项 {ItemId} 已不存在，跳过 finding 状态机。", run.Id, result.ChecklistItemId);
                continue;
            }

            var healthy = ResolveConsistentVerdict(result);
            if (healthy is null)
            {
                // 双判不一致、证据不足、执行出错（含 FrameworkVerdict=Mismatch）均不建 finding、不发通知。
                logger.LogDebug(
                    "执行 {RunId} 检查项 {ItemId} 双判未通过一致性校验（agent={Agent}，framework={Framework}），跳过 finding 状态机。",
                    run.Id,
                    item.Id,
                    result.AgentVerdict,
                    result.FrameworkVerdict);
                continue;
            }

            if (!item.IsJudgement)
            {
                // 信息型检查项只收集证据，不参与 finding 与告警。
                continue;
            }

            var dedupKey = BuildDedupKey(project.Id, run.EnvironmentId, run.ChecklistId, item.Id);
            var openFinding = await db.Findings
                .FirstOrDefaultAsync(
                    x => x.DedupKey == dedupKey && x.Status == FindingStatus.Open,
                    cancellationToken);

            // 规则版本变更：先以 RuleChanged 关闭旧 finding，再按新版本决定是否重开。
            if (openFinding is not null && openFinding.RuleVersion != item.RuleVersion)
            {
                await CloseForRuleChangedAsync(
                    db,
                    run,
                    project,
                    openFinding,
                    item,
                    checklistName,
                    environmentName,
                    timeZone,
                    now,
                    cancellationToken);
                openFinding = null;
            }

            if (healthy.Value)
            {
                if (openFinding is not null)
                {
                    await ResolveRecoveredAsync(
                        db,
                        run,
                        project,
                        openFinding,
                        checklistName,
                        environmentName,
                        timeZone,
                        now,
                        cancellationToken);
                }

                continue;
            }

            // 异常：已有 Open 只更新最近判定，不重复通知；否则新建 Open 并按级别决定是否通知。
            if (openFinding is null)
            {
                await OpenFindingAsync(
                    db,
                    run,
                    project,
                    item,
                    result,
                    dedupKey,
                    checklistName,
                    environmentName,
                    timeZone,
                    now,
                    cancellationToken);
            }
            else
            {
                openFinding.LastSeenAt = now;
                openFinding.Evidence = result.Evidence ?? openFinding.Evidence;
                openFinding.Severity = item.Severity!.Value;
                openFinding.RuleVersion = item.RuleVersion;
                openFinding.Title = item.Title;
                openFinding.Message = result.Narrative ?? openFinding.Message;
                await db.SaveChangesAsync(cancellationToken);
            }
        }
    }

    /// <summary>agent 与框架判定一致时返回 true=健康（Ok）、false=异常（Abnormal）；其余组合返回 null。</summary>
    private static bool? ResolveConsistentVerdict(CheckResult result) =>
        (result.AgentVerdict, result.FrameworkVerdict) switch
        {
            (VerdictStatus.Ok, FrameworkVerdictStatus.Ok) => true,
            (VerdictStatus.Abnormal, FrameworkVerdictStatus.Abnormal) => false,
            _ => null,
        };

    private async Task OpenFindingAsync(
        GrowthInspectorDbContext db,
        ChecklistRun run,
        Project project,
        ChecklistItem item,
        CheckResult result,
        string dedupKey,
        string checklistName,
        string environmentName,
        TimeZoneInfo timeZone,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var finding = new Finding
        {
            ProjectId = project.Id,
            EnvironmentId = run.EnvironmentId,
            ChecklistId = run.ChecklistId,
            ChecklistItemId = item.Id,
            DedupKey = dedupKey,
            Severity = item.Severity!.Value,
            Status = FindingStatus.Open,
            RuleVersion = item.RuleVersion,
            Title = item.Title,
            Message = result.Narrative ?? result.ErrorMessage,
            Evidence = result.Evidence,
            FirstSeenAt = now,
            LastSeenAt = now,
        };
        db.Findings.Add(finding);

        if (IsAlertable(finding.Severity))
        {
            var evidenceSummary = Truncate(result.Evidence, larkOptions.Value.EvidenceSummaryMaxChars);
            var occurredAt = InspectionTimeZone.Format(now, timeZone);
            var markdown = LarkMessageTemplates.FindingOpened(
                project.Name,
                environmentName,
                checklistName,
                item.Title,
                finding.Severity,
                finding.RuleVersion,
                evidenceSummary,
                finding.Message,
                occurredAt);

            var payload = new
            {
                kind = LarkNotificationKind.FindingOpened.ToString(),
                findingId = finding.Id,
                projectId = project.Id,
                projectName = project.Name,
                environmentId = run.EnvironmentId,
                environmentName,
                checklistId = run.ChecklistId,
                checklistName,
                checklistItemId = item.Id,
                itemTitle = item.Title,
                severity = finding.Severity.ToString(),
                ruleVersion = finding.RuleVersion,
                evidenceSummary,
                message = finding.Message,
                firstSeenAt = finding.FirstSeenAt,
                lastSeenAt = finding.LastSeenAt,
                occurredAt = now,
            };

            var succeeded = await TrySendAsync(
                db,
                run,
                project,
                finding,
                LarkNotificationKind.FindingOpened,
                markdown,
                payload,
                now,
                cancellationToken);
            if (succeeded)
            {
                finding.LastNotifiedAt = now;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "finding 打开：{DedupKey}（级别 {Severity}，规则版本 {RuleVersion}，runId={RunId}）。",
            finding.DedupKey,
            finding.Severity,
            finding.RuleVersion,
            run.Id);
    }

    private async Task ResolveRecoveredAsync(
        GrowthInspectorDbContext db,
        ChecklistRun run,
        Project project,
        Finding finding,
        string checklistName,
        string environmentName,
        TimeZoneInfo timeZone,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        finding.Status = FindingStatus.Resolved;
        finding.CloseReason = FindingCloseReason.Recovered;
        finding.ResolvedAt = now;

        if (ShouldNotify(finding))
        {
            var resolvedAt = InspectionTimeZone.Format(now, timeZone);
            var markdown = LarkMessageTemplates.FindingResolved(
                project.Name,
                environmentName,
                checklistName,
                finding.Title,
                finding.Severity,
                finding.RuleVersion,
                InspectionTimeZone.Format(finding.FirstSeenAt, timeZone),
                InspectionTimeZone.Format(finding.LastSeenAt, timeZone),
                resolvedAt);

            var payload = new
            {
                kind = LarkNotificationKind.FindingResolved.ToString(),
                findingId = finding.Id,
                projectId = project.Id,
                projectName = project.Name,
                environmentId = run.EnvironmentId,
                environmentName,
                checklistId = run.ChecklistId,
                checklistName,
                checklistItemId = finding.ChecklistItemId,
                itemTitle = finding.Title,
                severity = finding.Severity.ToString(),
                ruleVersion = finding.RuleVersion,
                closeReason = FindingCloseReason.Recovered.ToString(),
                firstSeenAt = finding.FirstSeenAt,
                lastSeenAt = finding.LastSeenAt,
                resolvedAt = now,
            };

            var succeeded = await TrySendAsync(
                db,
                run,
                project,
                finding,
                LarkNotificationKind.FindingResolved,
                markdown,
                payload,
                now,
                cancellationToken);
            if (succeeded)
            {
                finding.LastNotifiedAt = now;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "finding 恢复：{DedupKey}（规则版本 {RuleVersion}，runId={RunId}）。",
            finding.DedupKey,
            finding.RuleVersion,
            run.Id);
    }

    private async Task CloseForRuleChangedAsync(
        GrowthInspectorDbContext db,
        ChecklistRun run,
        Project project,
        Finding finding,
        ChecklistItem item,
        string checklistName,
        string environmentName,
        TimeZoneInfo timeZone,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var oldRuleVersion = finding.RuleVersion;
        finding.Status = FindingStatus.Resolved;
        finding.CloseReason = FindingCloseReason.RuleChanged;
        finding.ResolvedAt = now;

        if (ShouldNotify(finding))
        {
            var evidenceSummary = Truncate(finding.Evidence, larkOptions.Value.EvidenceSummaryMaxChars);
            var occurredAt = InspectionTimeZone.Format(now, timeZone);
            var markdown = LarkMessageTemplates.RuleChanged(
                project.Name,
                environmentName,
                checklistName,
                item.Title,
                finding.Severity,
                oldRuleVersion,
                item.RuleVersion,
                evidenceSummary,
                occurredAt);

            var payload = new
            {
                kind = LarkNotificationKind.RuleChanged.ToString(),
                findingId = finding.Id,
                projectId = project.Id,
                projectName = project.Name,
                environmentId = run.EnvironmentId,
                environmentName,
                checklistId = run.ChecklistId,
                checklistName,
                checklistItemId = item.Id,
                itemTitle = item.Title,
                severity = finding.Severity.ToString(),
                oldRuleVersion,
                newRuleVersion = item.RuleVersion,
                ruleVersion = item.RuleVersion,
                closeReason = FindingCloseReason.RuleChanged.ToString(),
                evidenceSummary,
                firstSeenAt = finding.FirstSeenAt,
                lastSeenAt = finding.LastSeenAt,
                resolvedAt = now,
                occurredAt = now,
            };

            var succeeded = await TrySendAsync(
                db,
                run,
                project,
                finding,
                LarkNotificationKind.RuleChanged,
                markdown,
                payload,
                now,
                cancellationToken);
            if (succeeded)
            {
                finding.LastNotifiedAt = now;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "finding 因判据变更关闭：{DedupKey}（{OldRuleVersion} → {NewRuleVersion}，runId={RunId}）。",
            finding.DedupKey,
            oldRuleVersion,
            item.RuleVersion,
            run.Id);
    }

    private async Task<bool> TrySendAsync(
        GrowthInspectorDbContext db,
        ChecklistRun run,
        Project project,
        Finding finding,
        LarkNotificationKind kind,
        string markdown,
        object payload,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var payloadJson = LarkPayloadJson.Serialize(payload);
        var dispatch = await larkDispatcher.SendAsync(
            project.LarkWebhookUrl,
            markdown,
            payloadJson,
            cancellationToken);

        db.LarkNotifications.Add(new LarkNotification
        {
            ProjectId = project.Id,
            ChecklistRunId = run.Id,
            FindingId = finding.Id,
            Kind = kind,
            IsMock = dispatch.IsMock,
            PayloadJson = payloadJson,
            Succeeded = dispatch.Succeeded,
            ErrorMessage = dispatch.ErrorMessage,
            CreatedAt = now,
        });

        if (!dispatch.Succeeded)
        {
            logger.LogWarning(
                "Lark 通知发送失败（kind={Kind}，finding={FindingId}）：{Error}",
                kind,
                finding.Id,
                dispatch.ErrorMessage);
        }

        return dispatch.Succeeded;
    }

    private static bool ShouldNotify(Finding finding) =>
        finding.LastNotifiedAt is not null || IsAlertable(finding.Severity);

    private static bool IsAlertable(Severity severity) => severity != Severity.Info;

    private static string Truncate(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var limit = Math.Max(50, maxChars);
        var normalized = value.Trim();
        return normalized.Length <= limit ? normalized : normalized[..limit] + "…";
    }

    /// <summary>去重键：{projectId}:{environmentId}:{checklistId}:{checklistItemId}。</summary>
    public static string BuildDedupKey(Guid projectId, Guid environmentId, Guid checklistId, Guid checklistItemId) =>
        $"{projectId}:{environmentId}:{checklistId}:{checklistItemId}";
}
