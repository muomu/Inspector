namespace GrowthInspector.Core.Models;

/// <summary>
/// Lark 通知留痕：新异常 / 恢复 / 判据变更 / 日报各写一条，v1 mock 模式下不真实发送，
/// 只记录发送意图与报文，供页面、验收与日报核对。
/// </summary>
public sealed class LarkNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    /// <summary>关联的执行轮次（日报为空）。</summary>
    public Guid? ChecklistRunId { get; set; }

    public ChecklistRun? ChecklistRun { get; set; }

    /// <summary>关联的 finding（日报为空）。</summary>
    public Guid? FindingId { get; set; }

    public LarkNotificationKind Kind { get; set; } = LarkNotificationKind.FindingOpened;

    /// <summary>v1 走 mock 通道时为 true（不真实发送）。</summary>
    public bool IsMock { get; set; } = true;

    /// <summary>发送报文（Markdown 或卡片 JSON）。</summary>
    public string PayloadJson { get; set; } = "{}";

    public bool Succeeded { get; set; } = true;

    public string? ErrorMessage { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
