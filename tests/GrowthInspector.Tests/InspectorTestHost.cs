using GrowthInspector.Agents;
using GrowthInspector.Agents.Chat;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Options;
using GrowthInspector.Data;
using GrowthInspector.Orchestration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GrowthInspector.Tests;

/// <summary>测试宿主：临时 SQLite 库 + 真实 Data / Agents / Orchestration 注册，可按需覆盖服务。</summary>
internal sealed class InspectorTestHost : IAsyncDisposable
{
    private readonly ServiceProvider serviceProvider;

    private InspectorTestHost(ServiceProvider serviceProvider, string databasePath)
    {
        this.serviceProvider = serviceProvider;
        DatabasePath = databasePath;
    }

    public string DatabasePath { get; }

    public IServiceProvider Services => serviceProvider;

    public IDbContextFactory<GrowthInspectorDbContext> DbFactory =>
        serviceProvider.GetRequiredService<IDbContextFactory<GrowthInspectorDbContext>>();

    public static async Task<InspectorTestHost> CreateAsync(
        Action<IServiceCollection>? beforeBuilder = null,
        Action<IServiceCollection>? afterBuilder = null,
        Action<InspectionOptions>? configureInspector = null,
        Action<DeepSeekOptions>? configureDeepSeek = null,
        TimeProvider? timeProvider = null)
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"growth-inspector-test-{Guid.NewGuid():N}.db");

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.Configure<InspectionOptions>(options =>
        {
            options.TimeZone = "UTC";
            options.SchedulerTickSeconds = 1;
            configureInspector?.Invoke(options);
        });
        services.Configure<DeepSeekOptions>(options =>
        {
            options.TimeoutSeconds = 2;
            options.ThinkingForSummary = true;
            configureDeepSeek?.Invoke(options);
        });

        if (timeProvider is not null)
        {
            services.AddSingleton(timeProvider);
        }

        beforeBuilder?.Invoke(services);

        services.AddGrowthInspectorData(databasePath);
        services.AddGrowthInspectorAgents(new DeepSeekOptions());

        // 测试默认不联网：LLM 配置固定为「未配置 key」，并把执行 / 摘要两个角色都覆盖成桩客户端
        // （真实注册是惰性的 LlmChatClient，按 scope 读配置）。需要断言摘要的用例可在 afterBuilder 里再覆盖。
        var stubChatClient = new StubChatClient();
        services.AddSingleton<ILlmSettingsStore>(
            new FixedLlmSettingsStore(new LlmSettingsSnapshot(null, null, null, null)));
        services.AddSingleton<IChatClient>(stubChatClient);
        services.AddScoped(_ => new DeepSeekChatClients(stubChatClient, stubChatClient));

        services.AddGrowthInspectorOrchestration();

        afterBuilder?.Invoke(services);

        var provider = services.BuildServiceProvider();
        await provider.EnsureGrowthInspectorDatabaseAsync();

        return new InspectorTestHost(provider, databasePath);
    }

    public async ValueTask DisposeAsync()
    {
        await serviceProvider.DisposeAsync();
        SqliteConnection.ClearAllPools();

        if (File.Exists(DatabasePath))
        {
            File.Delete(DatabasePath);
        }
    }
}

/// <summary>测试默认 IChatClient：不访问网络，返回固定摘要与 token 用量。</summary>
internal sealed class StubChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "测试摘要"))
        {
            Usage = new UsageDetails { InputTokenCount = 1, OutputTokenCount = 1 },
        });

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("测试 stub 不支持流式响应。");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>可手动推进的测试时钟。</summary>
internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}
