using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Data;
using Microsoft.EntityFrameworkCore;

namespace GrowthInspector.Web.Services;

/// <summary>日报应用服务：生成内容并落库（同一日期覆盖已有日报）。</summary>
public sealed class DailyReportService(
    IDbContextFactory<GrowthInspectorDbContext> dbContextFactory,
    IDailyReportGenerator generator)
{
    public async Task<Guid> GenerateAsync(DateOnly reportDate, CancellationToken cancellationToken = default)
    {
        var content = await generator.BuildAsync(reportDate, cancellationToken);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var summary = await db.AiSummaries
            .FirstOrDefaultAsync(
                x => x.Kind == AiSummaryKind.DailyReport && x.ReportDate == reportDate,
                cancellationToken);

        if (summary is null)
        {
            summary = new AiSummary
            {
                Kind = AiSummaryKind.DailyReport,
                ReportDate = reportDate,
            };
            db.AiSummaries.Add(summary);
        }

        summary.Content = content;
        await db.SaveChangesAsync(cancellationToken);

        return summary.Id;
    }
}
