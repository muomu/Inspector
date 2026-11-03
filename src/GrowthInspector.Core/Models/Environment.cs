namespace GrowthInspector.Core.Models;

/// <summary>项目下的环境（prod / beta 等）。执行时选定，finding 状态按环境隔离。</summary>
public sealed class Environment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>是否为项目默认环境。</summary>
    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
