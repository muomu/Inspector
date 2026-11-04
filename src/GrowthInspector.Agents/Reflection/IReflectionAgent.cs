using GrowthInspector.Core.Abstractions;

namespace GrowthInspector.Agents.Reflection;

/// <summary>反思 agent 声称完成的一次手册改写。</summary>
public sealed record ReflectionWrite(Guid RunbookId, string? Section, string? Summary);

/// <summary>反思结果：是否完成、结论文本、声称的改写清单和错误。</summary>
public sealed record ReflectionOutcome(
    bool Attempted,
    string? Narrative,
    IReadOnlyList<ReflectionWrite> Writes,
    string? ErrorMessage);

/// <summary>
/// 反思 agent：根据本轮执行结果判断是否需要补充/修改运维手册。
/// 工具集只含手册检索 / 披露 / 列表与手册写，不含管理 CRUD。
/// </summary>
public interface IReflectionAgent
{
    Task<ReflectionOutcome> ReflectAsync(
        ChecklistItemExecutionRequest request,
        ChecklistItemExecutionResult result,
        CancellationToken cancellationToken = default);
}
