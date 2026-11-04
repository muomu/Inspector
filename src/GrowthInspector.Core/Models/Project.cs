namespace GrowthInspector.Core.Models;

/// <summary>接入方项目：一个 Project 是一套独立的巡检数据域。</summary>
public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>项目名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>项目标识（短代码，用于展示与唯一性校验）。</summary>
    public string? Code { get; set; }

    /// <summary>项目级 Lark webhook；日报与告警都发到这里。</summary>
    public string? LarkWebhookUrl { get; set; }

    /// <summary>项目级运维上下文（Markdown），会随手册一并交给 agent。</summary>
    public string? OpsContext { get; set; }

    /// <summary>归档标记（v1 删除语义只有归档，无硬删 / 回收站）。</summary>
    public bool IsArchived { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Environment> Environments { get; set; } = new List<Environment>();

    public ICollection<Runbook> Runbooks { get; set; } = new List<Runbook>();

    public ICollection<Checklist> Checklists { get; set; } = new List<Checklist>();

    public ICollection<McpServer> McpServers { get; set; } = new List<McpServer>();
}
