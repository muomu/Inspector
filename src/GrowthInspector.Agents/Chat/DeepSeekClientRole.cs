namespace GrowthInspector.Agents.Chat;

/// <summary>DeepSeek 客户端的用途：执行 agent 与摘要 agent 的模型、thinking 默认值不同。</summary>
public enum DeepSeekClientRole
{
    Execution = 0,
    Summary = 1,
}
