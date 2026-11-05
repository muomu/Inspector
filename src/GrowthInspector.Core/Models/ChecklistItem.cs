namespace GrowthInspector.Core.Models;

/// <summary>
/// 检查项：最小可执行单元，1:N 从属于检查单、不可跨单复用。
/// 检查项只有自然语言描述（标题 + 正文）与严重级别：执行 agent 按正文取证，
/// 并依正文里的健康口径自行判定 Ok / Abnormal / InsufficientEvidence。
/// </summary>
public sealed class ChecklistItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChecklistId { get; set; }

    public Checklist? Checklist { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>检查项正文：查什么 / 什么算健康 / 异常先看哪（一段自然语言）。</summary>
    public string Body { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    /// <summary>严重级别：决定 finding 的告警级别；为空时不参与告警。</summary>
    public Severity? Severity { get; set; }

    public bool IsArchived { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
