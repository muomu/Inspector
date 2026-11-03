namespace GrowthInspector.Core.Abstractions;

/// <summary>Lark 通知通道：按 Project webhook 发送 Markdown 消息。</summary>
public interface ILarkNotifier
{
    Task SendMarkdownAsync(string webhookUrl, string markdown, CancellationToken cancellationToken = default);
}
