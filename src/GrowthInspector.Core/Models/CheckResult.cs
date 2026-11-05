namespace GrowthInspector.Core.Models;

/// <summary>单个检查项在一轮执行中的结果与证据。</summary>
public sealed class CheckResult
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChecklistRunId { get; set; }

    public ChecklistRun? ChecklistRun { get; set; }

    public Guid ChecklistItemId { get; set; }

    public ChecklistItem? ChecklistItem { get; set; }

    /// <summary>最终结果：由 agent 判定转换而来（Ok / Abnormal / InsufficientEvidence / Error）。</summary>
    public CheckResultStatus Status { get; set; } = CheckResultStatus.Error;

    /// <summary>agent 给出的判定（取证后的 claim）。</summary>
    public VerdictStatus? AgentVerdict { get; set; }

    /// <summary>agent 的叙述性说明（narrative）。</summary>
    public string? Narrative { get; set; }

    /// <summary>规则外发现（observations[]，JSON 数组），只进日报观察段。</summary>
    public string? ObservationsJson { get; set; }

    /// <summary>该检查项本轮的工具调用次数。</summary>
    public int ToolCallCount { get; set; }

    /// <summary>判定依据：工具调用结果摘录（JSON 或 Markdown）。</summary>
    public string? Evidence { get; set; }

    public long DurationMs { get; set; }

    public int PromptTokens { get; set; }

    public int CompletionTokens { get; set; }

    /// <summary>prompt tokens 中命中 provider 前缀缓存的部分（用于观测缓存命中率）。</summary>
    public int CachedPromptTokens { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
