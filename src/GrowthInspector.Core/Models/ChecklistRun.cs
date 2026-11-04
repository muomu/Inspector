namespace GrowthInspector.Core.Models;

/// <summary>一轮检查单执行。执行时写快照，保证历史解释可回溯。</summary>
public sealed class ChecklistRun
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChecklistId { get; set; }

    public Checklist? Checklist { get; set; }

    public Guid EnvironmentId { get; set; }

    public Environment? Environment { get; set; }

    /// <summary>执行时的检查单快照（含通用 / 引用专用手册全文），JSON。</summary>
    public string SnapshotJson { get; set; } = "{}";

    /// <summary>本轮触发来源：调度或手动。</summary>
    public ChecklistTrigger Trigger { get; set; } = ChecklistTrigger.Manual;

    public RunStatus Status { get; set; } = RunStatus.Pending;

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }

    public long DurationMs { get; set; }

    public int PromptTokens { get; set; }

    public int CompletionTokens { get; set; }

    /// <summary>失败原因（骨架阶段用于标注未接入的链路）。</summary>
    public string? ErrorMessage { get; set; }

    public ICollection<CheckResult> Results { get; set; } = new List<CheckResult>();

    public ICollection<ToolCallLog> ToolCalls { get; set; } = new List<ToolCallLog>();

    public AiSummary? Summary { get; set; }
}
