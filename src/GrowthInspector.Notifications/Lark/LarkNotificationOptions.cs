namespace GrowthInspector.Notifications.Lark;

/// <summary>
/// Lark 通知通道运行期配置。v1 默认 mock：只写 LarkNotification 留痕，不发真实请求。
/// 宿主可通过 <c>services.Configure&lt;LarkNotificationOptions&gt;(...)</c> 覆盖。
/// </summary>
public sealed class LarkNotificationOptions
{
    /// <summary>mock 模式：不真实发送，落库时 IsMock = true（v1 默认开启）。</summary>
    public bool MockMode { get; set; } = true;

    /// <summary>证据摘要入库 / 入报文的最大字符数（超出按模板截断）。</summary>
    public int EvidenceSummaryMaxChars { get; set; } = 400;

    /// <summary>日报中单条 AI 摘要的最大字符数。</summary>
    public int AiSummaryMaxChars { get; set; } = 500;

    /// <summary>日报没有任何数据时是否仍然发送；默认不发，避免每天产生空日报。</summary>
    public bool SendEmptyDailyReports { get; set; }
}
