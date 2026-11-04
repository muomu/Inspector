namespace GrowthInspector.Core.Models;

/// <summary>
/// 异常项（finding）。按 (Project, Environment, Checklist, ChecklistItem) 四元组去重：
/// 首次异常发一次 Lark，恢复后发一次，持续期间只进日报。
/// </summary>
public sealed class Finding
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    public Guid EnvironmentId { get; set; }

    public Guid ChecklistId { get; set; }

    public Guid ChecklistItemId { get; set; }

    /// <summary>去重键，格式见 Issue 正文：project:environment:checklist:item。</summary>
    public string DedupKey { get; set; } = string.Empty;

    public Severity Severity { get; set; } = Severity.Info;

    public FindingStatus Status { get; set; } = FindingStatus.Open;

    /// <summary>产生该 finding 时的检查项规则版本；规则变更后旧 finding 以 RuleChanged 关闭。</summary>
    public int RuleVersion { get; set; } = 1;

    /// <summary>关闭原因：恢复或判据变更；Open 时为空。</summary>
    public FindingCloseReason? CloseReason { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Message { get; set; }

    /// <summary>最近一次判定证据。</summary>
    public string? Evidence { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>最近一次发 Lark 的时间（新异常 / 恢复通知）。</summary>
    public DateTimeOffset? LastNotifiedAt { get; set; }
}
