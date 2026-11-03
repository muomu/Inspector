namespace GrowthInspector.Core.Models;

/// <summary>检查单 ↔ 运维手册的 N:M 引用。执行时把引用到的全部手册全文交给模型。</summary>
public sealed class ChecklistRunbookRef
{
    public Guid ChecklistId { get; set; }

    public Checklist? Checklist { get; set; }

    public Guid RunbookId { get; set; }

    public Runbook? Runbook { get; set; }
}
