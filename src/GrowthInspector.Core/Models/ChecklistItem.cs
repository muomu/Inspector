namespace GrowthInspector.Core.Models;

/// <summary>
/// 检查项：最小可执行单元，1:N 从属于检查单、不可跨单复用。
/// 判定型检查项声明结构化断言（工具名 / 参数模板 / 取值路径 / 比较符 / 阈值 / 窗口 / 级别 / RuleVersion），
/// 由 agent 取证、框架从 ToolCallLog 复核；无断言的检查项为信息型，只收集证据、不参与告警。
/// </summary>
public sealed class ChecklistItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChecklistId { get; set; }

    public Checklist? Checklist { get; set; }

    public string Title { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    /// <summary>数据源提示（给 agent 的排查方向）。</summary>
    public string? DataSourceHint { get; set; }

    /// <summary>预期说明（自然语言，供 agent 理解）。</summary>
    public string? Expectation { get; set; }

    // ---- 结构化断言：判定权在框架，agent 只负责取证与给出 claim ----

    /// <summary>断言使用的工具名（MCP 工具或本地工具，带前缀）。</summary>
    public string? ToolName { get; set; }

    /// <summary>工具调用参数模板（JSON 对象，支持占位符）。</summary>
    public string? ArgumentsTemplate { get; set; }

    /// <summary>从工具返回 JSON 中取值的路径（如 $.value）。</summary>
    public string? ValuePath { get; set; }

    /// <summary>比较符；为空表示信息型检查项。</summary>
    public ComparisonOperator? Operator { get; set; }

    /// <summary>阈值（数值或字符串，按比较符解释）。</summary>
    public string? Threshold { get; set; }

    /// <summary>取值窗口（如 1h、24h），供工具参数与说明使用。</summary>
    public string? Window { get; set; }

    /// <summary>严重级别；为空时不参与告警。</summary>
    public Severity? Severity { get; set; }

    /// <summary>规则版本：判据变更时递增，旧 finding 以「判据变更」关闭。</summary>
    public int RuleVersion { get; set; } = 1;

    public bool IsArchived { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 是否声明了可复核的结构化断言。
    /// 断言描述健康状态：取值满足 Operator/Threshold = Ok，不满足 = Abnormal。
    /// </summary>
    public bool HasAssertion =>
        !string.IsNullOrWhiteSpace(ToolName)
        && Operator is not null
        && !string.IsNullOrWhiteSpace(Threshold);

    /// <summary>是否判定型：有结构化断言且有严重级别。</summary>
    public bool IsJudgement => HasAssertion && Severity is not null;
}
