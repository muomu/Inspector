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

    public string ToolName { get; set; } = string.Empty;

    /// <summary>调用参数（截断后）。</summary>
    public string? Arguments { get; set; }

    /// <summary>调用结果（截断后）。</summary>
    public string? Result { get; set; }

    public bool Succeeded { get; set; }

    public long DurationMs { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
