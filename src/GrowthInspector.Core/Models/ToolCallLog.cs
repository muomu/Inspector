namespace GrowthInspector.Core.Models;

/// <summary>
/// 工具调用审计：记录工具名、参数、结果（按配置截断）与耗时。
/// 框架不强制护栏，只做完整审计。
/// </summary>
public sealed class ToolCallLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChecklistRunId { get; set; }

    public ChecklistRun? ChecklistRun { get; set; }

    public Guid? ChecklistItemId { get; set; }

    /// <summary>发起调用的 agent 角色（执行 / 反思 / 摘要）。</summary>
    public AgentRole? AgentRole { get; set; }

    /// <summary>MCP server 登记 Id（本地工具为空）。</summary>
    public Guid? McpServerId { get; set; }

    /// <summary>MCP server 名称（本地工具为空）。</summary>
    public string? ServerName { get; set; }

    public string ToolName { get; set; } = string.Empty;

    /// <summary>调用参数（截断后）。</summary>
    public string? Arguments { get; set; }

    /// <summary>调用结果（截断后）。</summary>
    public string? Result { get; set; }

    /// <summary>结果是否因超长被截断（截断外字段按证据不足处理）。</summary>
    public bool Truncated { get; set; }

    public bool Succeeded { get; set; }

    public long DurationMs { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
