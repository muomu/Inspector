namespace GrowthInspector.Core.Abstractions;

/// <summary>
/// finding 判定入口：在一轮执行结束后取该轮 CheckResult 的 agent / 框架双判，
/// 一致才进入 finding 状态机并按需发送 Lark（v1 mock）；不一致不建 finding、不告警。
/// 由 TPG-2713 实现，编排层（TPG-2707）在整轮结束后调用。
/// </summary>
public interface IFindingNotifier
{
    Task EvaluateRunAsync(Guid checklistRunId, CancellationToken cancellationToken = default);
}
