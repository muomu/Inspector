using GrowthInspector.Agents.Tools;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Core.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Agents.Reflection;

/// <summary>
/// 反思 agent 实现：每个检查项一次会话，工具集只含手册检索 / 披露 / 列表与手册写；
/// 按模型决定调用写工具（经 IRunbookWriteService 落库并写 ToolCallLog），最后解析反思结论。
/// </summary>
public sealed class ReflectionAgent(
    IChatClient chatClient,
    AgentToolsetFactory toolsetFactory,
    IOptions<InspectionOptions> options,
    ILogger<ReflectionAgent> logger) : IReflectionAgent
{
    public async Task<ReflectionOutcome> ReflectAsync(
        ChecklistItemExecutionRequest request,
        ChecklistItemExecutionResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);

        var timeoutSeconds = Math.Max(1, options.Value.AgentItemTimeoutSeconds);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var toolset = await toolsetFactory.CreateAsync(
                new AgentToolsetRequest(
                    request.ProjectId,
                    request.EnvironmentId,
                    request.ChecklistRunId,
                    request.Item.ItemId,
                    AgentRole.Reflection),
                timeoutCts.Token);

            var messages = ReflectionPromptBuilder.Build(request, result);
            var loop = await AgentToolLoop.RunAsync(
                chatClient,
                toolset,
                messages,
                options.Value.AgentMaxTurns,
                timeoutCts.Token);

            if (loop.FinalText is null)
            {
                var reason = loop.ErrorMessage ?? "模型未给出最终反思结论。";
                logger.LogWarning("反思 agent 未完成（runId={RunId}, item={ItemId}）：{Reason}",
                    request.ChecklistRunId, request.Item.ItemId, reason);
                return new ReflectionOutcome(false, null, [], reason);
            }

            if (!ReflectionPromptBuilder.TryParse(loop.FinalText, out var outcome, out var parseError)
                || outcome is null)
            {
                return new ReflectionOutcome(false, null, [], parseError);
            }

            return outcome;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ReflectionOutcome(false, null, [], $"反思执行超时（{timeoutSeconds} 秒）。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "反思 agent 执行失败（runId={RunId}, item={ItemId}）。",
                request.ChecklistRunId, request.Item.ItemId);
            return new ReflectionOutcome(false, null, [], $"反思 agent 执行失败：{ex.Message}");
        }
    }
}
