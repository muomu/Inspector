namespace GrowthInspector.Core.Models;

/// <summary>
/// 检查单：一次可调度执行的单元。归属 Project，执行时选定 Environment；
/// 一张检查单只有一个频率，可引用同 Project 下多份运维手册（N:M）。
/// </summary>
public sealed class Checklist
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public ChecklistFrequency Frequency { get; set; } = ChecklistFrequency.Manual;

    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<ChecklistItem> Items { get; set; } = new List<ChecklistItem>();

    public ICollection<ChecklistRunbookRef> RunbookRefs { get; set; } = new List<ChecklistRunbookRef>();
}
