using System.Text;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Data;
using Microsoft.EntityFrameworkCore;

namespace GrowthInspector.Web.Services;

/// <summary>
/// 生成指定日期的日报 Markdown。骨架阶段只做规则化汇总；
/// AI 摘要与 Lark 发送由 TPG-2711 / TPG-2714 接入。
/// </summary>
public sealed class DailyReportGenerator(IDbContextFactory<GrowthInspectorDbContext> dbContextFactory)
    : IDailyReportGenerator
{
    public async Task<string> BuildAsync(DateOnly reportDate, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var dayStart = new DateTimeOffset(reportDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var dayEnd = dayStart.AddDays(1);

        var runs = await db.ChecklistRuns
            .Where(x => x.StartedAt >= dayStart && x.StartedAt < dayEnd)
            .Include(x => x.Checklist)
            .Include(x => x.Environment)
            .OrderBy(x => x.StartedAt)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var openFindings = await db.Findings
            .Where(x => x.Status == FindingStatus.Open)
            .OrderByDescending(x => x.LastSeenAt)
            .Take(50)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var builder = new StringBuilder();
        builder.AppendLine($"# 巡检日报 {reportDate:yyyy-MM-dd}");
        builder.AppendLine();
        builder.AppendLine($"- 执行轮次：{runs.Count}（成功 {runs.Count(x => x.Status == RunStatus.Succeeded)} / 失败 {runs.Count(x => x.Status == RunStatus.Failed)}）");
        builder.AppendLine($"- 未恢复异常：{openFindings.Count}");
        builder.AppendLine();

        builder.AppendLine("## 执行明细");
        if (runs.Count == 0)
        {
            builder.AppendLine("（无）");
        }
        else
        {
            foreach (var run in runs)
            {
                builder.AppendLine(
                    $"- {run.StartedAt.ToLocalTime():HH:mm} {run.Checklist?.Name ?? "(已删除)"} @ {run.Environment?.Name ?? "(已删除)"}：{run.Status}（{run.DurationMs} ms，{run.PromptTokens + run.CompletionTokens} token）");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 未恢复异常");
        if (openFindings.Count == 0)
        {
            builder.AppendLine("（无）");
        }
        else
        {
            foreach (var finding in openFindings)
            {
                builder.AppendLine(
                    $"- [{finding.Severity}] {finding.Title}（最近出现 {finding.LastSeenAt.ToLocalTime():MM-dd HH:mm}）");
            }
        }

        builder.AppendLine();
        builder.AppendLine("> AI 摘要与 Lark 发送由 TPG-2711 / TPG-2714 接入。");

        return builder.ToString();
    }
}
