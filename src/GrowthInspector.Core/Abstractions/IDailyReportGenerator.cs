namespace GrowthInspector.Core.Abstractions;

/// <summary>生成指定日期的巡检日报（Markdown）：失败项、异常项与 AI 摘要。</summary>
public interface IDailyReportGenerator
{
    Task<string> BuildAsync(DateOnly reportDate, CancellationToken cancellationToken = default);
}
