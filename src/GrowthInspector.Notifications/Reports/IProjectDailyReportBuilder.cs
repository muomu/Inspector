namespace GrowthInspector.Notifications.Reports;

/// <summary>
/// 单个 Project 某日的日报生成入口：内容只包含该 Project 的执行、finding、观察与 AI 摘要，
/// 供日报后台服务按 Project 发送，也供 Web 的 IDailyReportGenerator 聚合展示。
/// </summary>
public interface IProjectDailyReportBuilder
{
    Task<ProjectDailyReport> BuildAsync(
        Guid projectId,
        DateOnly reportDate,
        CancellationToken cancellationToken = default);
}

/// <summary>单个 Project 的日报内容与判定数据。</summary>
public sealed record ProjectDailyReport(
    Guid ProjectId,
    string ProjectName,
    DateOnly ReportDate,
    string Markdown,
    bool HasData,
    string? LarkWebhookUrl,
    DailyReportCounters Counters);

/// <summary>日报概览里的计数，Web 聚合与验收可直接读取，不必再解析 Markdown。</summary>
public sealed record DailyReportCounters(
    int Runs,
    int Succeeded,
    int Failed,
    int Skipped,
    int Canceled,
    int NewFindings,
    int RecoveredFindings,
    int OngoingFindings,
    int RuleChangedFindings,
    int FailedItems,
    int Observations);
