using System.Text;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Data;
using GrowthInspector.Notifications.Reports;
using Microsoft.EntityFrameworkCore;

namespace GrowthInspector.Web.Services;

/// <summary>
/// 生成指定日期的巡检日报 Markdown（TPG-2714）：Core 接口不变，
/// 内部逐 Project 调用 IProjectDailyReportBuilder 后聚合，保证页面与 Lark 使用同一份内容。
/// </summary>
public sealed class DailyReportGenerator(
    IDbContextFactory<GrowthInspectorDbContext> dbContextFactory,
    IProjectDailyReportBuilder projectDailyReportBuilder) : IDailyReportGenerator
{
    public async Task<string> BuildAsync(DateOnly reportDate, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var projects = await db.Projects
            .AsNoTracking()
            .Where(x => !x.IsArchived)
            .OrderBy(x => x.Name)
            .Select(x => new { x.Id, x.Name })
            .ToListAsync(cancellationToken);

        var builder = new StringBuilder();
        builder.AppendLine($"# 巡检日报 {reportDate:yyyy-MM-dd}");
        builder.AppendLine();

        if (projects.Count == 0)
        {
            builder.AppendLine("（无 Project）");
            return builder.ToString();
        }

        builder.AppendLine($"共 {projects.Count} 个 Project，以下为各 Project 内容。");
        builder.AppendLine();

        foreach (var project in projects)
        {
            var report = await projectDailyReportBuilder.BuildAsync(project.Id, reportDate, cancellationToken);
            builder.AppendLine(report.Markdown.TrimEnd());
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }
}
