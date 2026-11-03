using GrowthInspector.Core.Options;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;

namespace GrowthInspector.Agents.Chat;

/// <summary>按 DeepSeek（OpenAI 兼容）配置创建 IChatClient。</summary>
public static class DeepSeekChatClientFactory
{
    /// <summary>读取本地环境变量中的密钥；不入库、不进 Git。</summary>
    public const string ApiKeyEnvironmentVariable = "DEEPSEEK_API_KEY";

    public static IChatClient Create(DeepSeekOptions options, string apiKey, string? model = null)
    {
        var client = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(options.BaseUrl) });

        return client.GetChatClient(model ?? options.Model).AsIChatClient();
    }
}
