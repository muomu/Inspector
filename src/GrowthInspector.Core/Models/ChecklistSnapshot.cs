namespace GrowthInspector.Core.Models;

/// <summary>执行时写入 ChecklistRun.SnapshotJson 的检查单快照（含引用手册全文）。</summary>
public sealed record ChecklistSnapshot(
    Guid ChecklistId,
    string Name,
    ChecklistFrequency Frequency,
    IReadOnlyList<ChecklistSnapshotItem> Items,
    IReadOnlyList<RunbookSnapshot> Runbooks);

/// <summary>快照中的单个检查项定义。</summary>
public sealed record ChecklistSnapshotItem(
    Guid ItemId,
    string Title,
    int SortOrder,
    string? DataSourceHint,
    string? Expectation,
    string? Threshold,
    Severity? Severity);

/// <summary>快照中固化的手册全文；手册后续被编辑不影响历史执行解释。</summary>
public sealed record RunbookSnapshot(Guid RunbookId, string Title, string Body);
