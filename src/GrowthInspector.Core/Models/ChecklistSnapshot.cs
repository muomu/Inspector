namespace GrowthInspector.Core.Models;

/// <summary>
/// 执行时写入 ChecklistRun.SnapshotJson 的检查单快照。
/// 快照固化了执行当时的通用手册、被引用专用手册、检查项自然语言定义与 finding 现状，
/// 手册或检查项后续被编辑不影响历史执行解释。
/// </summary>
public sealed record ChecklistSnapshot(
    Guid ChecklistId,
    string Name,
    ChecklistFrequency Frequency,
    ChecklistTrigger Trigger,
    IReadOnlyList<ChecklistSnapshotItem> Items,
    IReadOnlyList<RunbookSnapshot> GeneralRunbooks,
    IReadOnlyList<RunbookSnapshot> ReferencedRunbooks,
    IReadOnlyList<FindingSnapshot> Findings);

/// <summary>快照中的单个检查项定义（标题 + 自然语言正文 + 严重级别）。</summary>
public sealed record ChecklistSnapshotItem(
    Guid ItemId,
    string Title,
    int SortOrder,
    string Body,
    Severity? Severity);

/// <summary>快照中固化的手册全文。</summary>
public sealed record RunbookSnapshot(Guid RunbookId, string Title, RunbookKind Kind, string Body);

/// <summary>快照中固化的检查项 finding 现状（用于复现判定上下文）。</summary>
public sealed record FindingSnapshot(
    Guid ChecklistItemId,
    FindingStatus Status,
    string? Title,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? ResolvedAt);
