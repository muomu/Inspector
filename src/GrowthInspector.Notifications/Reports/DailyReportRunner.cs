using System.Globalization;
using GrowthInspector.Core.Models;
using GrowthInspector.Data;
using GrowthInspector.Notifications.Lark;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Notifications.Reports;

/// <summary>单个 Project 的日报发送结果，供后台服务与测试断言使用。</summary>
public sealed record DailyReportDispatchResult(
    Guid ProjectId,
    string ProjectName,
    bool Sent,
    bool SkippedEmpty,
    Guid? NotificationId,
    bool IsMock,
    bool Succeeded);

/// <summary>
/// 逐 Project 生成并发送日报（TPG-2714）：每个 Project 使用自己的 webhook；
/// v1 mock 模式或无 webhook 时仍写 LarkNotification(Kind=DailyReport) 留痕，不联网；
/// 无数据默认不发，可通过 LarkNotificationOptions.SendEmptyDailyReports 打开。
/// </summary>
public sealed class DailyReportRunner(
    IDbContextFactory<GrowthInspectorDbContext> dbContextFactory,
    IProjectDailyReportBuilder reportBuilder,
    ILarkNotificationDispatcher larkDispatcher,
    IOptions<LarkNotificationOptions> larkOptions,
    TimeProvider timeProvider,
    ILogger<DailyReportRunner> logger)
{
    public async Task<IReadOnlyList<DailyReportDispatchResult>> RunAsync(
        DateOnly reportDate,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var projects = await db.Projects
            .AsNoTracking()
            .Where(x => !x.IsArchived)
            .OrderBy(x => x.Name)
            .Select(x => new { x.Id, x.Name })
            .ToListAsync(cancellationToken);

        var results = new List<DailyReportDispatchResult>(projects.Count);

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var report = await reportBuilder.BuildAsync(project.Id, reportDate, cancellationToken);

                if (!report.HasData && !larkOptions.Value.SendEmptyDailyReports)
                {
                    logger.LogInformation(
                        "Project {ProjectId} 在 {ReportDate} 无日报数据，按配置跳过发送。",
                        project.Id,
                        reportDate);
                    results.Add(new DailyReportDispatchResult(
                        project.Id,
                        report.ProjectName,
                        Sent: false,
                        SkippedEmpty: true,
                        NotificationId: null,
                        IsMock: false,
                        Succeeded: true));
                    continue;
                }

                var payloadJson = LarkPayloadJson.Serialize(new
                {
                    kind = LarkNotificationKind.DailyReport.ToString(),
                    projectId = project.Id,
                    projectName = report.ProjectName,
                    reportDate = reportDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    hasData = report.HasData,
                    counters = report.Counters,
                    markdown = report.Markdown,
                });

                var dispatch = await larkDispatcher.SendAsync(
                    report.LarkWebhookUrl,
                    report.Markdown,
                    payloadJson,
                    cancellationToken);

                var notification = new LarkNotification
                {
                    ProjectId = project.Id,
                    ChecklistRunId = null,
                    FindingId = null,
                    Kind = LarkNotificationKind.DailyReport,
                    IsMock = dispatch.IsMock,
                    PayloadJson = payloadJson,
                    Succeeded = dispatch.Succeeded,
                    ErrorMessage = dispatch.ErrorMessage,
                    CreatedAt = timeProvider.GetUtcNow(),
                };
                db.LarkNotifications.Add(notification);

                if (!dispatch.Succeeded)
                {
                    logger.LogWarning(
                        "Project {ProjectId} 的 {ReportDate} 日报发送失败：{Error}",
                        project.Id,
                        reportDate,
                        dispatch.ErrorMessage);
                }

                results.Add(new DailyReportDispatchResult(
                    project.Id,
                    report.ProjectName,
                    Sent: true,
                    SkippedEmpty: false,
                    NotificationId: notification.Id,
                    dispatch.IsMock,
                    dispatch.Succeeded));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 单个 Project 的日报失败不阻断其他 Project；记录后继续。
                logger.LogError(ex, "Project {ProjectId} 的 {ReportDate} 日报生成失败。", project.Id, reportDate);
                results.Add(new DailyReportDispatchResult(
                    project.Id,
                    project.Name,
                    Sent: false,
                    SkippedEmpty: false,
                    NotificationId: null,
                    IsMock: false,
                    Succeeded: false));
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return results;
    }
}
