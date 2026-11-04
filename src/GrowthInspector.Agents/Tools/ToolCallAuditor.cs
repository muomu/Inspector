using System.Text;
using GrowthInspector.Core.Models;
using GrowthInspector.Core.Options;
using GrowthInspector.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Agents.Tools;

/// <summary>
/// 工具调用审计：每次调用（含本地工具）写一条 ToolCallLog，参数与结果按 ToolResultMaxBytes 截断。
/// 审计失败只记录日志，不影响 agent 的取证流程。
/// </summary>
public sealed class ToolCallAuditor(
    IDbContextFactory<GrowthInspectorDbContext> dbContextFactory,
    IOptions<InspectionOptions> options,
    ILogger<ToolCallAuditor> logger)
{
    public async Task RecordAsync(
        AgentToolCallContext context,
        string toolName,
        Guid? mcpServerId,
        string? serverName,
        string argumentsJson,
        AgentToolInvocationResult result,
        long durationMs,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var maxBytes = Math.Max(256, options.Value.ToolResultMaxBytes);
            var (arguments, argumentsTruncated) = Truncate(argumentsJson, maxBytes);
            var (resultText, resultTruncated) = Truncate(result.Text, maxBytes);

            var log = new ToolCallLog
            {
                ChecklistRunId = context.ChecklistRunId,
                ChecklistItemId = context.ChecklistItemId,
                AgentRole = context.AgentRole,
                McpServerId = mcpServerId,
                ServerName = serverName,
                ToolName = toolName,
                Arguments = arguments,
                Result = resultText,
                Truncated = argumentsTruncated || resultTruncated || result.Truncated,
                Succeeded = result.Succeeded,
                DurationMs = durationMs,
                ErrorMessage = result.ErrorMessage,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            // 审计不跟随超时取消：即使检查项超时，也希望留下已经发生的调用。
            await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
            db.ToolCallLogs.Add(log);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "写工具调用审计失败（tool={ToolName}, runId={RunId}）。", toolName, context.ChecklistRunId);
        }
    }

    /// <summary>按 UTF-8 字节上限截断，保留完整字符；超出时追加省略号并标记截断。</summary>
    public static (string? Text, bool Truncated) Truncate(string? text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text) || Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return (text, false);
        }

        var builder = new StringBuilder(text.Length);
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (bytes + runeBytes > maxBytes)
            {
                break;
            }

            builder.Append(rune.ToString());
            bytes += runeBytes;
        }

        builder.Append('…');
        return (builder.ToString(), true);
    }
}
