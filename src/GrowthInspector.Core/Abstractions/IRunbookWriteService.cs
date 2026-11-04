namespace GrowthInspector.Core.Abstractions;

/// <summary>手册章节改写结果。</summary>
public sealed record RunbookSectionUpdateResult(bool Ok, string? Error = null);

/// <summary>
/// 手册改写入口：反思 agent 通过进程内直调管理 service 写手册，不经管理 MCP 回环。
/// 名称唯一、同 Project 引用与归档校验由管理 service 实现负责。
/// </summary>
public interface IRunbookWriteService
{
    /// <summary>
    /// 改写（或追加）指定手册的某一节。<paramref name="sectionHeading"/> 为空时追加到文末。
    /// </summary>
    Task<RunbookSectionUpdateResult> UpdateSectionAsync(
        Guid runbookId,
        string? sectionHeading,
        string newContent,
        CancellationToken cancellationToken = default);
}
