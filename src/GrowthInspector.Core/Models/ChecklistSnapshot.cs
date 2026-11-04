namespace GrowthInspector.Core.Models;

/// <summary>
/// 执行时写入 ChecklistRun.SnapshotJson 的检查单快照。
/// 快照固化了执行当时的通用手册、被引用专用手册、检查项断言与 finding 现状，
/// 手册后续被编辑不影响历史执行解释。
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

/// <summary>快照中的单个检查项定义（含结构化断言）。</summary>
public sealed record ChecklistSnapshotItem(
    Guid ItemId,
    string Title,
    int SortOrder,
    string? ToolName,
    string? ArgumentsTemplate,
    string? ValuePath,
    ComparisonOperator? Operator,
    string? Threshold,
    string? Window,
    Severity? Severity,
    int RuleVersion,
    string? DataSourceHint,
    string? Expectation);

/// <summary>快照中固化的手册全文。</summary>
public sealed record RunbookSnapshot(Guid RunbookId, string Title, RunbookKind Kind, string Body);

/// <summary>快照中固化的检查项 finding 现状（用于复现判定上下文）。</summary>
public sealed record FindingSnapshot(
    Guid ChecklistItemId,
    FindingStatus Status,
    int RuleVersion,
    string? Title,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? ResolvedAt);
