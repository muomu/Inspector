namespace GrowthInspector.Core.Models;

/// <summary>AI 摘要：单轮执行摘要，或每日 10:00 的日报。</summary>
public sealed class AiSummary
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>执行摘要关联的执行；日报为空。</summary>
    public Guid? ChecklistRunId { get; set; }

    public ChecklistRun? ChecklistRun { get; set; }

    public AiSummaryKind Kind { get; set; } = AiSummaryKind.RunSummary;

    /// <summary>日报日期（仅 DailyReport 有值）。</summary>
    public DateOnly? ReportDate { get; set; }

    /// <summary>Markdown 正文。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>生成使用的模型名。</summary>
    public string? Model { get; set; }

    /// <summary>是否因 LLM 故障使用了模板兜底（不编造结论）。</summary>
    public bool FallbackUsed { get; set; }

    public int PromptTokens { get; set; }

    public int CompletionTokens { get; set; }

    public long DurationMs { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
