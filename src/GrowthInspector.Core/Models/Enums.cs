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

/// <summary>检查项与 finding 的严重级别。</summary>
public enum Severity
{
    Info = 0,
    Warning = 1,
    Critical = 2,
}

/// <summary>finding 的状态：只有开放 / 已恢复两态，持续期间不迁移。</summary>
public enum FindingStatus
{
    Open = 0,
    Resolved = 1,
}

/// <summary>一轮检查单执行的状态。</summary>
public enum RunStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
}

/// <summary>单个检查项的执行判定结果。</summary>
public enum CheckResultStatus
{
    /// <summary>符合预期。</summary>
    Ok = 0,

    /// <summary>不符合预期（有阈值判定）。</summary>
    Abnormal = 1,

    /// <summary>证据不足，无法给出判定（信息型检查项的正常结果）。</summary>
    InsufficientEvidence = 2,

    /// <summary>执行出错（agent / MCP / LLM 故障）。</summary>
    Error = 3,
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
