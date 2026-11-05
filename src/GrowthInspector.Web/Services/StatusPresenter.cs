using GrowthInspector.Core.Models;

namespace GrowthInspector.Web.Services;

/// <summary>视觉状态：五态 + Running。颜色只是二次编码，文案与记号才是主表达。</summary>
public enum StatusState
{
    Ok,
    Warning,
    Danger,
    Pending,
    Neutral,
    Running,
}

/// <summary>检查项类型：当前所有检查项都参与判定（保留枚举供展示层扩展）。</summary>
public enum StatusItemKind
{
    Judgement,
    Informational,
}

/// <summary>展示形态：行内状态点，或 Finding 行尾的 chip。</summary>
public enum StatusPresentationMode
{
    Status,
    Chip,
}

/// <summary>
/// 单一状态展示结果：页面只消费这里，禁止再复制 switch、禁止用一个 class 表达多种语义。
/// </summary>
public sealed record StatusPresentation(
    StatusState State,
    string Label,
    string Glyph,
    string? Sub,
    string AriaLabel,
    StatusPresentationMode Mode = StatusPresentationMode.Status)
{
    public string CssClass => Mode == StatusPresentationMode.Chip ? ChipClass : StatusClass;

    public string NodeModifierClass => State switch
    {
        StatusState.Warning => "node--warn",
        StatusState.Danger => "node--bad",
        StatusState.Pending => "node--pending",
        StatusState.Neutral => "node--idle",
        _ => string.Empty,
    };

    public string FindingModifierClass => State switch
    {
        StatusState.Warning => "finding--warning",
        StatusState.Danger => "finding--critical",
        _ => string.Empty,
    };

    public string SeverityModifierClass => State switch
    {
        StatusState.Warning => "finding__severity--warning",
        StatusState.Danger => "finding__severity--critical",
        _ => "finding__severity--info",
    };

    private string StatusClass => State switch
    {
        StatusState.Ok => "status status--ok",
        StatusState.Warning => "status status--warn",
        StatusState.Danger => "status status--bad",
        StatusState.Pending => "status status--pending",
        StatusState.Neutral => "status status--idle",
        StatusState.Running => "status status--running",
        _ => "status status--idle",
    };

    private string ChipClass => State switch
    {
        StatusState.Ok => "chip chip--ok",
        StatusState.Warning => "chip chip--warning",
        StatusState.Danger => "chip chip--danger",
        _ => "chip",
    };
}

/// <summary>
/// 状态枚举到视觉语义的唯一映射。
/// Error / 证据不足都不是故障：统一「待证」，不给朱红。
/// Succeeded 只说明执行结束，文案写「完成」，不写「成功」，也不给通过绿。
/// </summary>
public static class StatusPresenter
{
    public static StatusPresentation FromResult(
        CheckResultStatus status,
        Severity? severity = null,
        StatusItemKind itemKind = StatusItemKind.Judgement) => status switch
    {
        CheckResultStatus.Ok => new(
            StatusState.Ok,
            "Ok",
            "✓",
            null,
            "Ok：符合检查项健康口径"),

        CheckResultStatus.Abnormal => FromAbnormal(severity),

        CheckResultStatus.InsufficientEvidence => itemKind == StatusItemKind.Informational
            ? new StatusPresentation(
                StatusState.Neutral,
                "信息",
                "—",
                "信息型检查项",
                "信息：信息型检查项正常收集证据，不参与告警")
            : new StatusPresentation(
                StatusState.Pending,
                "待证",
                "◇",
                "证据不足",
                "待证：证据不足，不是故障"),

        CheckResultStatus.Error => new StatusPresentation(
            StatusState.Pending,
            "待证",
            "◇",
            "Error",
            "待证：执行出错，不是故障"),

        _ => new StatusPresentation(
            StatusState.Neutral,
            "未判定",
            "—",
            null,
            "未判定"),
    };

    /// <summary>Finding 左侧的严重级标签；严重级与视觉态分离。</summary>
    public static StatusPresentation FromSeverity(Severity severity) => severity switch
    {
        Severity.Critical => new StatusPresentation(
            StatusState.Danger,
            "Critical",
            "■",
            null,
            "严重级 Critical"),

        Severity.Warning => new StatusPresentation(
            StatusState.Warning,
            "Warning",
            "▲",
            null,
            "严重级 Warning"),

        _ => new StatusPresentation(
            StatusState.Neutral,
            "Info",
            "—",
            null,
            "严重级 Info"),
    };

    /// <summary>执行生命周期：Succeeded 是「完成」，不是健康。</summary>
    public static StatusPresentation FromRun(RunStatus status) => status switch
    {
        RunStatus.Pending => new StatusPresentation(
            StatusState.Neutral,
            "等待中",
            "—",
            null,
            "等待中"),

        RunStatus.Running => new StatusPresentation(
            StatusState.Running,
            "执行中",
            "◉",
            null,
            "执行中"),

        RunStatus.Succeeded => new StatusPresentation(
            StatusState.Neutral,
            "完成",
            "—",
            null,
            "完成：执行已结束，不代表健康"),

        RunStatus.Failed => new StatusPresentation(
            StatusState.Danger,
            "失败",
            "■",
            null,
            "失败"),

        RunStatus.Canceled => new StatusPresentation(
            StatusState.Neutral,
            "已取消",
            "—",
            null,
            "已取消"),

        RunStatus.Skipped => new StatusPresentation(
            StatusState.Neutral,
            "已跳过",
            "—",
            null,
            "已跳过"),

        _ => new StatusPresentation(
            StatusState.Neutral,
            "未知",
            "—",
            null,
            "未知执行状态"),
    };

    /// <summary>Finding 生命周期 chip：未恢复 / 已恢复。</summary>
    public static StatusPresentation FromFinding(
        FindingStatus status,
        FindingCloseReason? closeReason = null) => status switch
    {
        FindingStatus.Open => new StatusPresentation(
            StatusState.Neutral,
            "未恢复",
            string.Empty,
            null,
            "未恢复：检查项仍未恢复",
            StatusPresentationMode.Chip),

        FindingStatus.Resolved => new StatusPresentation(
            StatusState.Ok,
            "已恢复",
            string.Empty,
            "检查恢复",
            "已恢复：检查项已恢复",
            StatusPresentationMode.Chip),

        _ => new StatusPresentation(
            StatusState.Neutral,
            "未知",
            string.Empty,
            null,
            "未知 Finding 状态",
            StatusPresentationMode.Chip),
    };

    /// <summary>本轮执行没有该检查项结果（缺失 / 未跑完）：按待证展示，不能读作健康。</summary>
    public static StatusPresentation MissingResult() => new(
        StatusState.Pending,
        "待证",
        "◇",
        "无结果",
        "待证：本轮执行没有该检查项结果");

    private static StatusPresentation FromAbnormal(Severity? severity) => severity switch
    {
        Severity.Critical => new StatusPresentation(
            StatusState.Danger,
            "异常",
            "■",
            "Critical",
            "异常：Critical 级异常"),

        Severity.Warning => new StatusPresentation(
            StatusState.Warning,
            "注意",
            "▲",
            "Warning",
            "注意：Warning 级异常"),

        Severity.Info => new StatusPresentation(
            StatusState.Neutral,
            "信息",
            "—",
            "Abnormal · Info",
            "信息：Abnormal 级信息型结果"),

        // 严重级缺失的 Abnormal 不能当成 Info：按待证处理，避免假“信息 / 正常”。
        _ => new StatusPresentation(
            StatusState.Pending,
            "待证",
            "◇",
            "未配置严重级别",
            "待证：Abnormal 结果未配置严重级别，不能读作信息"),
    };
}
