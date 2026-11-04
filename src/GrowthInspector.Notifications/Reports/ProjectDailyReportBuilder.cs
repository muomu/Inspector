using System.Text;
using System.Text.Json;
using GrowthInspector.Core.Models;
using GrowthInspector.Core.Options;
using GrowthInspector.Data;
using GrowthInspector.Notifications.Internal;
using GrowthInspector.Notifications.Lark;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Notifications.Reports;

/// <summary>
/// 按 Project 生成日报 Markdown（TPG-2714）：只读取该 Project 当日的执行、finding、观察与 AI 摘要，
/// 保证单 Project 发送的内容不串项目；日期边界按 InspectionOptions.TimeZone 计算。
/// </summary>
public sealed class ProjectDailyReportBuilder(
    IDbContextFactory<GrowthInspectorDbContext> dbContextFactory,
    IOptions<InspectionOptions> inspectionOptions,
    IOptions<LarkNotificationOptions> larkOptions,
    ILogger<ProjectDailyReportBuilder> logger) : IProjectDailyReportBuilder
{
    private static readonly JsonSerializerOptions ObservationsJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ProjectDailyReport> BuildAsync(
        Guid projectId,
        DateOnly reportDate,
        CancellationToken cancellationToken = default)
    {
        var timeZone = InspectionTimeZone.Resolve(inspectionOptions.Value.TimeZone);
        var (dayStart, dayEnd) = InspectionTimeZone.GetDayRange(reportDate, timeZone);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var project = await db.Projects
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} 不存在，无法生成日报。");

        var runs = await db.ChecklistRuns
            .AsNoTracking()
            .Where(x => x.StartedAt >= dayStart
                && x.StartedAt < dayEnd
                && x.Checklist != null
                && x.Checklist.ProjectId == projectId)
            .Include(x => x.Checklist)
            .Include(x => x.Environment)
            .Include(x => x.Results)
                .ThenInclude(x => x.ChecklistItem)
            .Include(x => x.Summary)
            .OrderBy(x => x.StartedAt)
            .ToListAsync(cancellationToken);

        var findings = await db.Findings
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .Where(x =>
                (x.Status == FindingStatus.Open && x.FirstSeenAt < dayEnd)
                || (x.Status == FindingStatus.Resolved
                    && x.ResolvedAt != null
                    && x.ResolvedAt >= dayStart
                    && x.ResolvedAt < dayEnd))
            .OrderBy(x => x.FirstSeenAt)
            .ToListAsync(cancellationToken);

        // finding 只存 Id，展示名称按当前检查单 / 检查项 / 环境回查。
        var checklistIds = findings.Select(x => x.ChecklistId).Distinct().ToList();
        var itemIds = findings.Select(x => x.ChecklistItemId).Distinct().ToList();
        var environmentIds = findings.Select(x => x.EnvironmentId).Distinct().ToList();

        var checklistNames = checklistIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Checklists.AsNoTracking()
                .Where(x => checklistIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var itemMeta = itemIds.Count == 0
            ? new Dictionary<Guid, (string Title, int RuleVersion)>()
            : await db.ChecklistItems.AsNoTracking()
                .Where(x => itemIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => new ValueTuple<string, int>(x.Title, x.RuleVersion), cancellationToken);
        var environmentNames = environmentIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Environments.AsNoTracking()
                .Where(x => environmentIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);

        var newFindings = findings
            .Where(x => x.Status == FindingStatus.Open && x.FirstSeenAt >= dayStart)
            .ToList();
        var ongoingFindings = findings
            .Where(x => x.Status == FindingStatus.Open && x.FirstSeenAt < dayStart)
            .ToList();
        var recoveredFindings = findings
            .Where(x => x.Status == FindingStatus.Resolved && x.CloseReason == FindingCloseReason.Recovered)
            .ToList();
        var ruleChangedFindings = findings
            .Where(x => x.Status == FindingStatus.Resolved && x.CloseReason == FindingCloseReason.RuleChanged)
            .ToList();

        var failedItems = runs
            .SelectMany(run => run.Results
                .Where(result => result.Status == CheckResultStatus.Error)
                .Select(result => new FailedItemEntry(
                    ChecklistName: run.Checklist?.Name ?? "（检查单已删除）",
                    EnvironmentName: run.Environment?.Name ?? "（环境已删除）",
                    ItemTitle: result.ChecklistItem?.Title ?? "（检查项已删除）",
                    ErrorMessage: result.ErrorMessage,
                    FrameworkVerdict: result.FrameworkVerdict?.ToString())))
            .ToList();

        var observations = runs
            .SelectMany(run => run.Results
                .SelectMany(result => ParseObservations(result.ObservationsJson)
                    .Select(text => new ObservationEntry(
                        ChecklistName: run.Checklist?.Name ?? "（检查单已删除）",
                        EnvironmentName: run.Environment?.Name ?? "（环境已删除）",
                        ItemTitle: result.ChecklistItem?.Title ?? "（检查项已删除）",
                        Text: text))))
            .ToList();

        var summaries = runs
            .Where(run => run.Summary is { } summary && !string.IsNullOrWhiteSpace(summary.Content))
            .Select(run => new SummaryEntry(
                ChecklistName: run.Checklist?.Name ?? "（检查单已删除）",
                EnvironmentName: run.Environment?.Name ?? "（环境已删除）",
                Content: Truncate(run.Summary!.Content, larkOptions.Value.AiSummaryMaxChars)))
            .ToList();

        var skippedRuns = runs.Where(x => x.Status == RunStatus.Skipped).ToList();
        var canceledRuns = runs.Where(x => x.Status == RunStatus.Canceled).ToList();

        var counters = new DailyReportCounters(
            Runs: runs.Count,
            Succeeded: runs.Count(x => x.Status == RunStatus.Succeeded),
            Failed: runs.Count(x => x.Status == RunStatus.Failed),
            Skipped: skippedRuns.Count,
            Canceled: canceledRuns.Count,
            NewFindings: newFindings.Count,
            RecoveredFindings: recoveredFindings.Count,
            OngoingFindings: ongoingFindings.Count,
            RuleChangedFindings: ruleChangedFindings.Count,
            FailedItems: failedItems.Count,
            Observations: observations.Count);

        var hasData = counters.Runs > 0
            || counters.NewFindings + counters.RecoveredFindings + counters.OngoingFindings + counters.RuleChangedFindings > 0
            || counters.FailedItems > 0
            || counters.Observations > 0
            || summaries.Count > 0;

        var markdown = BuildMarkdown(
            project.Name,
            reportDate,
            timeZone,
            counters,
            newFindings,
            recoveredFindings,
            ongoingFindings,
            ruleChangedFindings,
            failedItems,
            observations,
            summaries,
            skippedRuns,
            canceledRuns,
            checklistNames,
            itemMeta,
            environmentNames);

        logger.LogDebug(
            "已生成 Project {ProjectId} 的 {ReportDate} 日报（有数据={HasData}，执行 {Runs} 轮）。",
            projectId,
            reportDate,
            hasData,
            counters.Runs);

        return new ProjectDailyReport(
            project.Id,
            project.Name,
            reportDate,
            markdown,
            hasData,
            project.LarkWebhookUrl,
            counters);
    }

    private static string BuildMarkdown(
        string projectName,
        DateOnly reportDate,
        TimeZoneInfo timeZone,
        DailyReportCounters counters,
        IReadOnlyList<Finding> newFindings,
        IReadOnlyList<Finding> recoveredFindings,
        IReadOnlyList<Finding> ongoingFindings,
        IReadOnlyList<Finding> ruleChangedFindings,
        IReadOnlyList<FailedItemEntry> failedItems,
        IReadOnlyList<ObservationEntry> observations,
        IReadOnlyList<SummaryEntry> summaries,
        IReadOnlyList<ChecklistRun> skippedRuns,
        IReadOnlyList<ChecklistRun> canceledRuns,
        IReadOnlyDictionary<Guid, string> checklistNames,
        IReadOnlyDictionary<Guid, (string Title, int RuleVersion)> itemMeta,
        IReadOnlyDictionary<Guid, string> environmentNames)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"## {projectName} 巡检日报 {reportDate:yyyy-MM-dd}");
        builder.AppendLine();
        builder.AppendLine($"> 时区：{timeZone.Id}");
        builder.AppendLine();

        builder.AppendLine("## 概览");
        builder.AppendLine($"- 执行轮次：{counters.Runs}（成功 {counters.Succeeded} / 失败 {counters.Failed} / 跳过 {counters.Skipped} / 取消 {counters.Canceled}）");
        builder.AppendLine($"- finding：新增 {counters.NewFindings} / 恢复 {counters.RecoveredFindings} / 持续 {counters.OngoingFindings} / 判据变更 {counters.RuleChangedFindings}");
        builder.AppendLine($"- 失败检查项：{counters.FailedItems}");
        builder.AppendLine($"- 规则外观察：{counters.Observations}");
        builder.AppendLine();

        builder.AppendLine("## 新增异常");
        AppendFindings(builder, newFindings, checklistNames, itemMeta, environmentNames, timeZone, includeResolvedAt: false);
        builder.AppendLine();

        builder.AppendLine("## 恢复异常");
        AppendFindings(builder, recoveredFindings, checklistNames, itemMeta, environmentNames, timeZone, includeResolvedAt: true);
        builder.AppendLine();

        builder.AppendLine("## 持续异常");
        AppendFindings(builder, ongoingFindings, checklistNames, itemMeta, environmentNames, timeZone, includeResolvedAt: false);
        builder.AppendLine();

        builder.AppendLine("## 判据变更");
        AppendFindings(builder, ruleChangedFindings, checklistNames, itemMeta, environmentNames, timeZone, includeResolvedAt: true);
        builder.AppendLine();

        builder.AppendLine("## 失败检查项");
        if (failedItems.Count == 0)
        {
            builder.AppendLine("（无）");
        }
        else
        {
            foreach (var failed in failedItems)
            {
                var framework = string.IsNullOrWhiteSpace(failed.FrameworkVerdict) ? string.Empty : $"（框架复核：{failed.FrameworkVerdict}）";
                builder.AppendLine(
                    $"- {failed.ChecklistName} / {failed.ItemTitle} @ {failed.EnvironmentName}：{failed.ErrorMessage ?? "（无错误信息）"}{framework}");
            }
        }

        builder.AppendLine();

        builder.AppendLine("## 规则外观察");
        if (observations.Count == 0)
        {
            builder.AppendLine("（无）");
        }
        else
        {
            foreach (var observation in observations)
            {
                builder.AppendLine(
                    $"- {observation.ChecklistName} / {observation.ItemTitle} @ {observation.EnvironmentName}：{observation.Text}");
            }
        }

        builder.AppendLine();

        builder.AppendLine("## 执行异常");
        if (skippedRuns.Count == 0 && canceledRuns.Count == 0)
        {
            builder.AppendLine("（无）");
        }
        else
        {
            foreach (var run in skippedRuns)
            {
                builder.AppendLine(
                    $"- 跳过：{run.Checklist?.Name ?? "（检查单已删除）"} @ {run.Environment?.Name ?? "（环境已删除）"}（{InspectionTimeZone.Format(run.StartedAt, timeZone)}，{run.ErrorMessage ?? "同检查单同环境已在执行"}）");
            }

            foreach (var run in canceledRuns)
            {
                builder.AppendLine(
                    $"- 取消：{run.Checklist?.Name ?? "（检查单已删除）"} @ {run.Environment?.Name ?? "（环境已删除）"}（{InspectionTimeZone.Format(run.StartedAt, timeZone)}，{run.ErrorMessage ?? "执行被取消"}）");
            }
        }

        builder.AppendLine();

        builder.AppendLine("## 当日 AI 摘要");
        if (summaries.Count == 0)
        {
            builder.AppendLine("（无）");
        }
        else
        {
            foreach (var summary in summaries)
            {
                builder.AppendLine($"- {summary.ChecklistName} @ {summary.EnvironmentName}：{summary.Content}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendFindings(
        StringBuilder builder,
        IReadOnlyList<Finding> findings,
        IReadOnlyDictionary<Guid, string> checklistNames,
        IReadOnlyDictionary<Guid, (string Title, int RuleVersion)> itemMeta,
        IReadOnlyDictionary<Guid, string> environmentNames,
        TimeZoneInfo timeZone,
        bool includeResolvedAt)
    {
        if (findings.Count == 0)
        {
            builder.AppendLine("（无）");
            return;
        }

        foreach (var finding in findings)
        {
            var checklistName = checklistNames.GetValueOrDefault(finding.ChecklistId, "（检查单已删除）");
            var itemTitle = itemMeta.TryGetValue(finding.ChecklistItemId, out var item) ? item.Title : finding.Title;
            var environmentName = environmentNames.GetValueOrDefault(finding.EnvironmentId, "（环境已删除）");
            var line = $"- [{finding.Severity}] {finding.Title}（{checklistName} / {itemTitle} @ {environmentName}，首次 {InspectionTimeZone.Format(finding.FirstSeenAt, timeZone)}";

            if (includeResolvedAt && finding.ResolvedAt is { } resolvedAt)
            {
                line += $"，关闭 {InspectionTimeZone.Format(resolvedAt, timeZone)}";
            }
            else
            {
                line += $"，最近 {InspectionTimeZone.Format(finding.LastSeenAt, timeZone)}";
            }

            if (finding.CloseReason == FindingCloseReason.RuleChanged)
            {
                var currentRuleVersion = itemMeta.TryGetValue(finding.ChecklistItemId, out var ruleItem)
                    ? ruleItem.RuleVersion
                    : finding.RuleVersion;
                line += $"，规则版本 {finding.RuleVersion} → {currentRuleVersion}";
            }

            builder.AppendLine(line + "）");
        }
    }

    private static IReadOnlyList<string> ParseObservations(string? observationsJson)
    {
        if (string.IsNullOrWhiteSpace(observationsJson))
        {
            return [];
        }

        try
        {
            var values = JsonSerializer.Deserialize<string[]>(observationsJson, ObservationsJsonOptions);
            return values?
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            return [observationsJson.Trim()];
        }
    }

    private static string Truncate(string value, int maxChars)
    {
        var limit = Math.Max(100, maxChars);
        var normalized = value.Trim();
        return normalized.Length <= limit ? normalized : normalized[..limit] + "…";
    }

    private sealed record FailedItemEntry(
        string ChecklistName,
        string EnvironmentName,
        string ItemTitle,
        string? ErrorMessage,
        string? FrameworkVerdict);

    private sealed record ObservationEntry(
        string ChecklistName,
        string EnvironmentName,
        string ItemTitle,
        string Text);

    private sealed record SummaryEntry(
        string ChecklistName,
        string EnvironmentName,
        string Content);
}
