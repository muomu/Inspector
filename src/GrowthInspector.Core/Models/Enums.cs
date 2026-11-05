namespace GrowthInspector.Core.Models;

/// <summary>检查单的调度频率。一张检查单只有一个频率。</summary>
public enum ChecklistFrequency
{
    /// <summary>只允许手动触发。</summary>
    Manual = 0,

    /// <summary>每小时一次（对每个启用环境）。</summary>
    Hourly = 1,

    /// <summary>每天一次（本地时间 10:00 前后由调度器对齐）。</summary>
    Daily = 2,
}

/// <summary>检查单一次执行的触发来源。</summary>
public enum ChecklistTrigger
{
    /// <summary>按频率自动调度。</summary>
    Schedule = 0,

    /// <summary>页面或管理 MCP 手动触发。</summary>
    Manual = 1,
}

/// <summary>检查项与 finding 的严重级别。</summary>
public enum Severity
{
    Info = 0,
    Warning = 1,
    Critical = 2,
}

/// <summary>运维手册的类别：通用知识自动注入，专用知识检索 / 引用后注入。</summary>
public enum RunbookKind
{
    /// <summary>项目级通用知识，自动注入该项目下所有检查单。</summary>
    General = 0,

    /// <summary>专用知识，默认不注入，可被检索或被检查单显式引用。</summary>
    Specialized = 1,
}

/// <summary>finding 的状态：只有开放 / 已恢复两态，持续期间不迁移。</summary>
public enum FindingStatus
{
    Open = 0,
    Resolved = 1,
}

/// <summary>finding 关闭的原因。</summary>
public enum FindingCloseReason
{
    /// <summary>检查恢复正常。</summary>
    Recovered = 0,
}

/// <summary>一轮检查单执行的状态。</summary>
public enum RunStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,

    /// <summary>应用启动时清理的遗留 Running，或执行被取消。</summary>
    Canceled = 4,

    /// <summary>同一 (检查单, 环境) 已在执行，本次触发被跳过。</summary>
    Skipped = 5,
}

/// <summary>单个检查项的执行判定结果。</summary>
public enum CheckResultStatus
{
    /// <summary>符合预期（agent 判 Ok）。</summary>
    Ok = 0,

    /// <summary>不符合预期（agent 判 Abnormal）。</summary>
    Abnormal = 1,

    /// <summary>证据不足，无法给出判定（agent 如实说明）。</summary>
    InsufficientEvidence = 2,

    /// <summary>执行出错：agent / MCP / LLM 故障，或 claim 无法解析。</summary>
    Error = 3,
}

/// <summary>agent 给出的判定。</summary>
public enum VerdictStatus
{
    Ok = 0,
    Abnormal = 1,
    InsufficientEvidence = 2,
}

/// <summary>工具调用所属的 agent 角色（审计用）。</summary>
public enum AgentRole
{
    Execution = 0,
    Reflection = 1,
    Summary = 2,
}

/// <summary>Lark 通知的类型。</summary>
public enum LarkNotificationKind
{
    /// <summary>新异常（finding 打开）。</summary>
    FindingOpened = 0,

    /// <summary>异常恢复。</summary>
    FindingResolved = 1,

    /// <summary>每日 10:00 日报。</summary>
    DailyReport = 2,
}

/// <summary>AI 摘要的类型。</summary>
public enum AiSummaryKind
{
    /// <summary>单轮检查单执行摘要。</summary>
    RunSummary = 0,

    /// <summary>每日 10:00 的巡检日报。</summary>
    DailyReport = 1,
}

/// <summary>MCP server 的传输方式。</summary>
public enum McpTransport
{
    /// <summary>本地子进程（stdio）。</summary>
    Stdio = 0,

    /// <summary>HTTP / Streamable HTTP 端点。</summary>
    Http = 1,
}
