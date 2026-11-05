using System.Diagnostics;
using System.Text;
using GrowthInspector.Core.Models;
using GrowthInspector.Core.Options;
using GrowthInspector.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Orchestration.Summaries;

/// <summary>
/// 单轮执行摘要 agent：读整轮 CheckResult（含 agent 判定与 Error）、observations、
/// 耗时与 token，调用 IChatClient 生成 Markdown 摘要并落 AiSummary(Kind=RunSummary)。
/// LLM 未注册 / 无 key / 超时 / 返回空正文时使用规则化模板兜底，FallbackUsed=true，
/// 模板只陈述已落库事实，不编造结论。
/// </summary>
public sealed class RunSummaryService(
    IDbContextFactory<GrowthInspectorDbContext> dbContextFactory,
    IChatClient? chatClient,
    IOptions<DeepSeekOptions> deepSeekOptions,
    TimeProvider timeProvider,
    ILogger<RunSummaryService> logger) : IRunSummaryService
{
    private const string SystemPrompt =
        "你是巡检执行摘要助手。只能依据用户提供的已落库事实（执行状态、检查项结果、"
        + "agent 判定、证据、observations、错误、耗时与 token）生成中文 Markdown 摘要；"
        + "不得编造数据、根因或未出现的结论。异常与错误必须如实列出，无法判断时直接写明证据不足。"
        + "不要输出与事实无关的客套话，不要重复大段原始证据。";

    public async Task<Guid?> SummarizeRunAsync(
        Guid checklistRunId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var run = await db.ChecklistRuns
            .Include(x => x.Checklist)
            .Include(x => x.Environment)
            .Include(x => x.Results)
                .ThenInclude(x => x.ChecklistItem)
            .SingleOrDefaultAsync(x => x.Id == checklistRunId, cancellationToken);

        if (run is null)
        {
            logger.LogWarning("执行 {RunId} 不存在，无法生成摘要。", checklistRunId);
            return null;
        }

        var results = run.Results
            .OrderBy(x => x.ChecklistItem?.SortOrder ?? int.MaxValue)
            .ThenBy(x => x.CreatedAt)
            .ToList();

        var settings = deepSeekOptions.Value;
        var stopwatch = Stopwatch.StartNew();
        string content;
        string? model = null;
        var fallbackUsed = false;
        var promptTokens = 0;
        var completionTokens = 0;

        if (chatClient is null)
        {
            logger.LogInformation("IChatClient 未注册，执行 {RunId} 的摘要使用模板兜底。", checklistRunId);
            content = BuildFallbackSummary(run, results);
            fallbackUsed = true;
        }
        else
        {
            try
            {
                var response = await CallModelAsync(run, results, settings, cancellationToken);
                var text = response.Text?.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new InvalidOperationException("摘要模型返回空正文。");
                }

                content = text;
                model = response.ModelId ?? settings.SummaryModel;
                promptTokens = ToTokenCount(response.Usage?.InputTokenCount);
                completionTokens = ToTokenCount(response.Usage?.OutputTokenCount);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "执行 {RunId} 的 AI 摘要生成失败，使用模板兜底。", checklistRunId);
                content = BuildFallbackSummary(run, results);
                fallbackUsed = true;
                model = null;
                promptTokens = 0;
                completionTokens = 0;
            }
        }

        stopwatch.Stop();

        var summary = await db.AiSummaries
            .SingleOrDefaultAsync(x => x.ChecklistRunId == run.Id, cancellationToken);

        if (summary is null)
        {
            summary = new AiSummary
            {
                ChecklistRunId = run.Id,
                Kind = AiSummaryKind.RunSummary,
            };
            db.AiSummaries.Add(summary);
        }

        summary.Kind = AiSummaryKind.RunSummary;
        summary.Content = content;
        summary.Model = model;
        summary.FallbackUsed = fallbackUsed;
        summary.PromptTokens = promptTokens;
        summary.CompletionTokens = completionTokens;
        summary.DurationMs = stopwatch.ElapsedMilliseconds;
        summary.CreatedAt = timeProvider.GetUtcNow();

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "执行 {RunId} 的摘要已生成（fallback={FallbackUsed}，model={Model}，{DurationMs} ms，token {PromptTokens}+{CompletionTokens}）。",
            run.Id,
            fallbackUsed,
            model,
            summary.DurationMs,
            summary.PromptTokens,
            summary.CompletionTokens);

        return summary.Id;
    }

    private async Task<ChatResponse> CallModelAsync(
        ChecklistRun run,
        IReadOnlyList<CheckResult> results,
        DeepSeekOptions settings,
        CancellationToken cancellationToken)
    {
        var chatOptions = BuildChatOptions(settings);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, BuildPrompt(run, results)),
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds)));

        return await chatClient!.GetResponseAsync(messages, chatOptions, timeoutCts.Token);
    }

    private static ChatOptions BuildChatOptions(DeepSeekOptions settings)
    {
        var chatOptions = new ChatOptions
        {
            ModelId = settings.SummaryModel,
        };

        if (settings.ThinkingForSummary)
        {
            // 约定：M.E.AI 的 ReasoningOptions 是跨 provider 的 thinking 开关，
            // AdditionalProperties["thinking"] 是 DeepSeek OpenAI 兼容接口的透传字段。
            // 具体 provider 适配（TPG-2704）负责把两者映射到实际请求参数。
            chatOptions.Reasoning = new ReasoningOptions
            {
                Effort = ReasoningEffort.Medium,
            };
            chatOptions.AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["thinking"] = new Dictionary<string, string>
                {
                    ["type"] = "enabled",
                },
            };
        }

        return chatOptions;
    }

    private static string BuildPrompt(ChecklistRun run, IReadOnlyList<CheckResult> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine("## 本轮执行");
        builder.AppendLine($"- 检查单：{run.Checklist?.Name ?? "(未知)"}");
        builder.AppendLine($"- 环境：{run.Environment?.Name ?? "(未知)"}");
        builder.AppendLine($"- 触发方式：{run.Trigger}");
        builder.AppendLine($"- 执行状态：{run.Status}");
        builder.AppendLine($"- 开始时间：{run.StartedAt:O}");
        builder.AppendLine($"- 完成时间：{run.CompletedAt:O}");
        builder.AppendLine($"- 执行耗时：{run.DurationMs} ms");
        builder.AppendLine($"- 执行 token：输入 {run.PromptTokens} / 输出 {run.CompletionTokens}");
        if (!string.IsNullOrWhiteSpace(run.ErrorMessage))
        {
            builder.AppendLine($"- 整轮错误：{run.ErrorMessage}");
        }

        builder.AppendLine();
        builder.AppendLine($"## 检查项结果（{results.Count} 项）");
        if (results.Count == 0)
        {
            builder.AppendLine("本轮没有检查项结果。");
            return builder.ToString();
        }

        var index = 0;
        foreach (var result in results)
        {
            index++;
            builder.AppendLine($"### {index}. {result.ChecklistItem?.Title ?? "检查项已删除"}");
            builder.AppendLine($"- 最终结果：{result.Status}");
            builder.AppendLine($"- agent 判定：{FormatVerdict(result.AgentVerdict)}");

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                builder.AppendLine($"- 错误：{Truncate(result.ErrorMessage, 500)}");
            }

            if (!string.IsNullOrWhiteSpace(result.Narrative))
            {
                builder.AppendLine($"- 叙述：{Truncate(result.Narrative, 800)}");
            }

            if (!string.IsNullOrWhiteSpace(result.Evidence))
            {
                builder.AppendLine($"- 证据：{Truncate(result.Evidence, 800)}");
            }

            if (!string.IsNullOrWhiteSpace(result.ObservationsJson))
            {
                builder.AppendLine($"- observations：{Truncate(result.ObservationsJson, 800)}");
            }

            builder.AppendLine(
                $"- 耗时：{result.DurationMs} ms；token：{result.PromptTokens}+{result.CompletionTokens}；工具调用：{result.ToolCallCount}");
            builder.AppendLine();
        }

        builder.AppendLine("请输出：本轮结论、异常项清单（含错误与证据不足）、值得关注但与检查项无关的 observations、以及下一步建议。");
        return builder.ToString();
    }

    /// <summary>规则化兜底摘要：只汇总已落库事实，不做归因、不编造根因。</summary>
    private static string BuildFallbackSummary(ChecklistRun run, IReadOnlyList<CheckResult> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# 执行摘要：{run.Checklist?.Name ?? "(未知检查单)"} @ {run.Environment?.Name ?? "(未知环境)"}");
        builder.AppendLine();
        builder.AppendLine("> 模型摘要不可用（LLM 故障 / 未配置 / 超时 / 返回空正文），以下为规则化汇总，未使用模型生成结论。");
        builder.AppendLine();
        builder.AppendLine($"- 执行状态：{run.Status}");
        builder.AppendLine($"- 触发方式：{run.Trigger}");
        builder.AppendLine($"- 开始时间：{run.StartedAt:O}");
        builder.AppendLine($"- 完成时间：{run.CompletedAt:O}");
        builder.AppendLine($"- 执行耗时：{run.DurationMs} ms");
        builder.AppendLine($"- 执行 token：输入 {run.PromptTokens} / 输出 {run.CompletionTokens}");
        builder.AppendLine(
            $"- 检查项：{results.Count}（Ok {results.Count(x => x.Status == CheckResultStatus.Ok)}"
            + $" / Abnormal {results.Count(x => x.Status == CheckResultStatus.Abnormal)}"
            + $" / Error {results.Count(x => x.Status == CheckResultStatus.Error)}"
            + $" / InsufficientEvidence {results.Count(x => x.Status == CheckResultStatus.InsufficientEvidence)}）");

        if (!string.IsNullOrWhiteSpace(run.ErrorMessage))
        {
            builder.AppendLine($"- 整轮错误：{run.ErrorMessage}");
        }

        builder.AppendLine();
        builder.AppendLine("## 检查项明细");
        if (results.Count == 0)
        {
            builder.AppendLine("（无检查项结果）");
            return builder.ToString();
        }

        foreach (var result in results)
        {
            builder.AppendLine(
                $"- **[{result.Status}]** {result.ChecklistItem?.Title ?? "检查项已删除"}"
                + $"（agent={FormatVerdict(result.AgentVerdict)}，{result.DurationMs} ms）");
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                builder.AppendLine($"  - 错误：{Truncate(result.ErrorMessage, 300)}");
            }

            if (!string.IsNullOrWhiteSpace(result.Narrative))
            {
                builder.AppendLine($"  - 叙述：{Truncate(result.Narrative, 300)}");
            }
        }

        return builder.ToString();
    }

    private static string FormatVerdict<T>(T? verdict) where T : struct, Enum
        => verdict?.ToString() ?? "（无）";

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "…";

    private static int ToTokenCount(long? value)
        => value is null or <= 0 ? 0 : (int)Math.Min(int.MaxValue, value.Value);

    private static int ToTokenCount(int? value)
        => value is null or <= 0 ? 0 : value.Value;
}
