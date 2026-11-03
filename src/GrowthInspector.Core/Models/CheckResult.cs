namespace GrowthInspector.Core.Models;

/// <summary>单个检查项在一轮执行中的结果与证据。</summary>
public sealed class CheckResult
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChecklistRunId { get; set; }

    public ChecklistRun? ChecklistRun { get; set; }

    public Guid ChecklistItemId { get; set; }

    public ChecklistItem? ChecklistItem { get; set; }

    public CheckResultStatus Status { get; set; } = CheckResultStatus.Error;

    /// <summary>判定依据：工具调用结果摘录（JSON 或 Markdown）。</summary>
    public string? Evidence { get; set; }

    public long DurationMs { get; set; }

    public int PromptTokens { get; set; }

    public int CompletionTokens { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
