using Microsoft.Extensions.AI;

namespace GrowthInspector.Agents.Chat;

/// <summary>执行与摘要两个角色的 DeepSeek 客户端。</summary>
public sealed class DeepSeekChatClients(IChatClient execution, IChatClient summary) : IDisposable
{
    /// <summary>执行 agent 使用（模型与 thinking 来自 DeepSeek:Model / ThinkingForExecution）。</summary>
    public IChatClient Execution { get; } = execution;

    /// <summary>摘要 agent 使用（模型与 thinking 来自 DeepSeek:SummaryModel / ThinkingForSummary）。</summary>
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
