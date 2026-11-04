namespace GrowthInspector.Data;

/// <summary>审计清理的后台调度参数。</summary>
public sealed class AuditPruningOptions
{
    /// <summary>两次清理之间的间隔，默认 24 小时。</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>进程启动后等待多久做第一次清理，默认 5 分钟，避免和启动建库争用。</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(5);
}
