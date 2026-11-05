using Microsoft.Extensions.AI;

namespace GrowthInspector.Agents.Chat;

/// <summary>执行与摘要两个角色的 DeepSeek 客户端（共用模型与已保存的 LLM 配置，thinking 开关可不同）。</summary>
public sealed class DeepSeekChatClients(IChatClient execution, IChatClient summary) : IDisposable
{
    /// <summary>执行 agent 使用（模型来自已保存配置 / DeepSeek:Model 出厂值，thinking 来自 ThinkingForExecution）。</summary>
    public IChatClient Execution { get; } = execution;

    /// <summary>摘要 agent 使用（与执行同模型，thinking 来自 ThinkingForSummary）。</summary>
    public IChatClient Summary { get; } = summary;

    public void Dispose()
    {
        if (!ReferenceEquals(Execution, Summary))
        {
            Summary.Dispose();
        }

        Execution.Dispose();
    }
}
