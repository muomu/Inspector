using System.Net.Http.Json;
using GrowthInspector.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace GrowthInspector.Notifications.Lark;

/// <summary>Lark 群机器人 webhook 通知（骨架实现：文本消息；卡片排版由 TPG-2713/2714 细化）。</summary>
public sealed class LarkNotifier(HttpClient httpClient, ILogger<LarkNotifier> logger) : ILarkNotifier
{
    public async Task SendMarkdownAsync(string webhookUrl, string markdown, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            logger.LogInformation("Project 未配置 Lark webhook，跳过本次发送。");
            return;
        }

        var payload = new
        {
            msg_type = "text",
            content = new { text = markdown },
        };

        using var response = await httpClient.PostAsJsonAsync(webhookUrl, payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning(
                "Lark webhook 发送失败：HTTP {StatusCode}，响应 {Body}",
                (int)response.StatusCode,
                body);
            response.EnsureSuccessStatusCode();
        }
    }
}
