namespace GrowthInspector.Core.Models;

/// <summary>
/// 运维手册：纯 Markdown 的长期记忆，承载技术栈、指标语义、告警规则与排查经验。
/// 不解析、不承载可执行判定；检查单通过 ChecklistRunbookRef 引用它。
/// </summary>
public sealed class Runbook
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>Markdown 正文。</summary>
    public string Body { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
