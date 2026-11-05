using System.Text.Json;
using Microsoft.Extensions.AI;

namespace GrowthInspector.Agents.Tools;

/// <summary>一次 agent 多轮取证的执行结果。</summary>
public sealed record AgentLoopResult(
    string? FinalText,
    int Turns,
    int PromptTokens,
    int CompletionTokens,
    bool MaxTurnsReached,
    string? ErrorMessage);

/// <summary>
/// 通用的「模型 → 工具 → 模型」循环：maxTurns > 0 时最多跑 maxTurns 轮，maxTurns <= 0 表示不限轮次；
/// 期间把工具结果回传给模型，模型不再发起工具调用时的文本即最终输出。工具调用审计由 AgentToolset 负责。
/// </summary>
public static class AgentToolLoop
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<AgentLoopResult> RunAsync(
        IChatClient chatClient,
        AgentToolset toolset,
        List<ChatMessage> messages,
        int maxTurns,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(toolset);
        ArgumentNullException.ThrowIfNull(messages);

        var turnLimit = maxTurns > 0 ? maxTurns : int.MaxValue;
        var promptTokens = 0;
        var completionTokens = 0;

        for (var turn = 1; turn <= turnLimit; turn++)
        {
            var options = new ChatOptions
            {
                Tools = toolset.Tools.Count > 0 ? toolset.Tools.ToList() : null,
                Temperature = 0f,
            };

            var response = await chatClient.GetResponseAsync(messages, options, cancellationToken);
            promptTokens += ToInt(response.Usage?.InputTokenCount);
            completionTokens += ToInt(response.Usage?.OutputTokenCount);

            if (response.Messages.Count > 0)
            {
                messages.AddRange(response.Messages);
            }
            else if (!string.IsNullOrEmpty(response.Text))
            {
                messages.Add(new ChatMessage(ChatRole.Assistant, response.Text));
            }

            var toolCalls = response.Messages
                .SelectMany(message => message.Contents)
                .OfType<FunctionCallContent>()
                .Where(call => !string.IsNullOrWhiteSpace(call.Name))
                .ToList();

            if (toolCalls.Count == 0)
            {
                return new AgentLoopResult(
                    response.Text,
                    turn,
                    promptTokens,
                    completionTokens,
                    MaxTurnsReached: false,
                    ErrorMessage: null);
            }

            foreach (var call in toolCalls)
            {
                var argumentsJson = SerializeArguments(call.Arguments);
                var result = await toolset.InvokeAsync(call.Name!, argumentsJson, cancellationToken);
                messages.Add(new ChatMessage(
                    ChatRole.Tool,
                    new List<AIContent>
                    {
                        new FunctionResultContent(call.CallId, result.Text),
                    }));
            }
        }

        return new AgentLoopResult(
            FinalText: null,
            Turns: turnLimit,
            promptTokens,
            completionTokens,
            MaxTurnsReached: true,
            ErrorMessage: $"达到工具调用轮次上限（{turnLimit}），模型未给出最终结论。");
    }

    private static string SerializeArguments(IDictionary<string, object?>? arguments)
        => arguments is null ? "{}" : JsonSerializer.Serialize(arguments, JsonOptions);

    private static int ToInt(long? value) => value is > int.MaxValue ? int.MaxValue : (int)(value ?? 0);
}
