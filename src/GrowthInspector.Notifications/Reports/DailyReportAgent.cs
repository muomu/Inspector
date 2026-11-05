using System.Diagnostics;
using System.Text;
using GrowthInspector.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace GrowthInspector.Notifications.Reports;

/// <summary>日报 agent 的输入事实：全部来自当日已落库的执行记录与 finding，不依赖执行时摘要。</summary>
public sealed record DailyReportFacts(
    string ProjectName,
    DateOnly ReportDate,
    DailyReportCounters Counters,
    IReadOnlyList<string> FindingLines,
    IReadOnlyList<string> FailedItemLines,
    IReadOnlyList<string> ObservationLines);

/// <summary>日报 agent 的产出：Markdown 摘要段与计量；兜底时 FallbackUsed = true。</summary>
public sealed record DailyReportAgentResult(
    string Content,
    bool FallbackUsed,
    string? Model,
    int PromptTokens,
    int CompletionTokens,
    long DurationMs);

public interface IDailyReportAgent
{
    Task<DailyReportAgentResult> SummarizeAsync(DailyReportFacts facts, CancellationToken cancellationToken = default);
}

/// <summary>
/// 日报 agent：每天每 Project 一次，读当日执行记录事实生成一段中文 Markdown 摘要。
/// LLM 未注册 / 无 key / 超时 / 返回空正文时退回规则化模板，模板只陈述已落库事实。
/// </summary>
public sealed class DailyReportAgent(
    IChatClient? chatClient,
    ILogger<DailyReportAgent> logger) : IDailyReportAgent
{
    private const string SystemPrompt =
        "你是巡检日报摘要助手。只能依据用户提供的当日执行事实（轮次统计、异常、失败检查项、规则外观察）"
        + "生成一段中文 Markdown 摘要；不得编造数据、根因或未出现的结论。异常与失败必须如实列出，"
        + "没有异常就明确写当日无异常。输出控制在 200 字以内，不要罗列全部明细，日报正文里已有明细。";

    public async Task<DailyReportAgentResult> SummarizeAsync(
        DailyReportFacts facts,
        CancellationToken cancellationToken = default)
    {
        if (chatClient is null)
        {
            return Fallback(facts, "未注册 LLM 客户端。");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await chatClient.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, SystemPrompt),
                new ChatMessage(ChatRole.User, BuildUserPrompt(facts)),
            ],
                new ChatOptions { Temperature = 0f },
                cancellationToken);

            var text = response.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return Fallback(facts, "模型返回空正文。");
            }

            return new DailyReportAgentResult(
                text,
                FallbackUsed: false,
                Model: null,
                ToInt(response.Usage?.InputTokenCount),
                ToInt(response.Usage?.OutputTokenCount),
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "日报 agent 生成失败（{ProjectName} {ReportDate}），使用规则模板兜底。",
                facts.ProjectName, facts.ReportDate);
            return Fallback(facts, ex.Message);
        }
    }

    private static string BuildUserPrompt(DailyReportFacts facts)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Project：{facts.ProjectName}");
        builder.AppendLine($"日期：{facts.ReportDate:yyyy-MM-dd}");
        builder.AppendLine($"执行轮次：{facts.Counters.Runs}（成功 {facts.Counters.Succeeded} / 失败 {facts.Counters.Failed} / 跳过 {facts.Counters.Skipped} / 取消 {facts.Counters.Canceled}）");
        builder.AppendLine($"finding：新增 {facts.Counters.NewFindings} / 恢复 {facts.Counters.RecoveredFindings} / 持续 {facts.Counters.OngoingFindings}");
        builder.AppendLine($"失败检查项：{facts.Counters.FailedItems} 个；规则外观察：{facts.Counters.Observations} 条");
        builder.AppendLine();

        AppendSection(builder, "当日异常（新增与持续）", facts.FindingLines);
        AppendSection(builder, "失败检查项", facts.FailedItemLines);
        AppendSection(builder, "规则外观察", facts.ObservationLines);
        return builder.ToString();
    }

    private static void AppendSection(StringBuilder builder, string title, IReadOnlyList<string> lines)
    {
        builder.AppendLine($"## {title}");
        if (lines.Count == 0)
        {
            builder.AppendLine("（无）");
            return;
        }

        foreach (var line in lines)
        {
            builder.AppendLine($"- {line}");
        }

        builder.AppendLine();
    }

    private static DailyReportAgentResult Fallback(DailyReportFacts facts, string? reason)
        => new(
            $"{facts.ProjectName} {facts.ReportDate:yyyy-MM-dd}：执行 {facts.Counters.Runs} 轮"
            + $"（成功 {facts.Counters.Succeeded} / 失败 {facts.Counters.Failed} / 跳过 {facts.Counters.Skipped} / 取消 {facts.Counters.Canceled}），"
            + $"新增异常 {facts.Counters.NewFindings}、恢复 {facts.Counters.RecoveredFindings}、持续 {facts.Counters.OngoingFindings}；"
            + $"失败检查项 {facts.Counters.FailedItems} 个，规则外观察 {facts.Counters.Observations} 条。",
            FallbackUsed: true,
            Model: null,
            0,
            0,
            0);

    private static int ToInt(long? value) => value is > int.MaxValue ? int.MaxValue : (int)(value ?? 0);
}
