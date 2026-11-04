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

    /// <summary>检查项判据（RuleVersion）变更，旧规则关闭。</summary>
    RuleChanged = 1,
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

/// <summary>单个检查项的执行判定结果（综合 agent 判定与框架复核）。</summary>
public enum CheckResultStatus
{
    /// <summary>符合预期（agent 与框架一致判 Ok，或信息型检查项正常收集证据）。</summary>
    Ok = 0,

    /// <summary>不符合预期（agent 与框架一致判 Abnormal）。</summary>
    Abnormal = 1,

    /// <summary>证据不足，无法给出判定（信息型检查项的正常结果）。</summary>
    InsufficientEvidence = 2,

    /// <summary>执行出错：agent / MCP / LLM 故障，或复核不一致。</summary>
    Error = 3,
}

/// <summary>agent 给出的判定。</summary>
public enum VerdictStatus
{
    Ok = 0,
    Abnormal = 1,
    InsufficientEvidence = 2,
}

/// <summary>框架按结构化断言从 ToolCallLog 重算得到的判定。</summary>
public enum FrameworkVerdictStatus
{
    Ok = 0,
    Abnormal = 1,
    InsufficientEvidence = 2,

    /// <summary>重算失败（证据缺失 / 取值越界 / 断言无法执行），按 Error 处理。</summary>
    Error = 3,

    /// <summary>与 agent 判定不一致，按 Error 处理、不产生 finding。</summary>
    Mismatch = 4,
}

/// <summary>
/// 结构化断言的比较符。
/// 语义：断言描述「健康」状态 —— 取值满足断言 = Ok，不满足 = Abnormal（与告警表达式相反，转写时需取反）。
/// 数值比较用 invariant decimal；Eq/Ne 在双方可解析为数值时按数值比较，否则按序数（ordinal）字符串比较；
/// Contains/NotContains 按序数子串匹配。
/// </summary>
public enum ComparisonOperator
{
    /// <summary>取值 &lt; 阈值。</summary>
    Lt = 0,

    /// <summary>取值 &lt;= 阈值。</summary>
    Lte = 1,

    /// <summary>取值 &gt; 阈值。</summary>
    Gt = 2,

    /// <summary>取值 &gt;= 阈值。</summary>
    Gte = 3,

    /// <summary>取值 == 阈值（数值或字符串）。</summary>
    Eq = 4,

    /// <summary>取值 != 阈值。</summary>
    Ne = 5,

    /// <summary>取值包含阈值子串（字符串）。</summary>
    Contains = 6,

    /// <summary>取值不包含阈值子串（字符串）。</summary>
    NotContains = 7,
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

    /// <summary>判据变更导致旧 finding 关闭。</summary>
    RuleChanged = 2,

    /// <summary>每日 10:00 日报。</summary>
    DailyReport = 3,
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
