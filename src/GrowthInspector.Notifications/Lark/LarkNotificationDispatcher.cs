using GrowthInspector.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Notifications.Lark;

/// <summary>一次 Lark 发送尝试的结果；mock 通道视为成功，只留痕。</summary>
public sealed record LarkDispatchResult(bool IsMock, bool Succeeded, string? ErrorMessage);

/// <summary>
/// Lark 发送抽象：mock 模式（或未配置 webhook）不发起真实请求、只返回发送意图；
/// 真实模式委托 <see cref="ILarkNotifier"/>，失败降级为「记录失败」而不抛出。
/// </summary>
public interface ILarkNotificationDispatcher
{
    /// <summary>当前是否处于 mock 模式。</summary>
    bool MockMode { get; }

    Task<LarkDispatchResult> SendAsync(
        string? webhookUrl,
        string markdown,
        string payloadJson,
        CancellationToken cancellationToken = default);
}

/// <summary>默认实现：v1 全部走 mock；真实发送失败只记日志并落失败结果。</summary>
public sealed class LarkNotificationDispatcher(
    ILarkNotifier notifier,
    IOptions<LarkNotificationOptions> options,
    ILogger<LarkNotificationDispatcher> logger) : ILarkNotificationDispatcher
{
    public bool MockMode => options.Value.MockMode;

    public async Task<LarkDispatchResult> SendAsync(
        string? webhookUrl,
        string markdown,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        // mock 模式与「未配置 webhook」都只记录发送意图，不联网（AC 要求无 webhook 也留痕）。
        if (MockMode || string.IsNullOrWhiteSpace(webhookUrl))
        {
            logger.LogInformation(
                "Lark mock 通道：通知已留痕（mock={MockMode}，配置 webhook={HasWebhook}）。",
                MockMode,
                !string.IsNullOrWhiteSpace(webhookUrl));
            return new LarkDispatchResult(true, true, null);
        }

        try
        {
            await notifier.SendMarkdownAsync(webhookUrl, markdown, cancellationToken);
            return new LarkDispatchResult(false, true, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Lark 发送失败，按降级记录（不向调用方抛出）。");
            return new LarkDispatchResult(false, false, ex.Message);
        }
    }
}
