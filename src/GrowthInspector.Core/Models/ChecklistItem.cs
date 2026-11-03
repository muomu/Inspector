namespace GrowthInspector.Core.Models;

/// <summary>
/// 检查项：最小可执行单元，1:N 从属于检查单、不可跨单复用。
/// 声明数据源提示、预期 / 阈值、严重级别；无阈值与级别时只收集证据、不参与告警。
/// </summary>
public sealed class ChecklistItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChecklistId { get; set; }

    public Checklist? Checklist { get; set; }

    public string Title { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    /// <summary>数据源提示（给 agent 的排查方向，不是可执行配置）。</summary>
    public string? DataSourceHint { get; set; }

    /// <summary>预期说明。</summary>
    public string? Expectation { get; set; }

    /// <summary>阈值表达式（存在即判定型）。</summary>
    public string? Threshold { get; set; }

    /// <summary>严重级别（存在即参与告警）。</summary>
    public Severity? Severity { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>是否判定型：同时声明阈值与级别。</summary>
    public bool IsJudgement => Threshold is not null && Severity is not null;
}
