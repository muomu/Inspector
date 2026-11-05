namespace GrowthInspector.Orchestration.Summaries;

/// <summary>
/// 单轮执行摘要：整轮结束后把全部 CheckResult（含 agent 判定与 Error）、
/// observations、耗时与 token 交给摘要模型，生成 AiSummary(Kind=RunSummary)。
/// LLM 不可用时写规则化模板兜底，不编造结论。
/// </summary>
public interface IRunSummaryService
{
    Task<Guid?> SummarizeRunAsync(Guid checklistRunId, CancellationToken cancellationToken = default);
}
