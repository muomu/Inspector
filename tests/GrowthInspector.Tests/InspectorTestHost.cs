using GrowthInspector.Agents;
using GrowthInspector.Core.Options;
using GrowthInspector.Data;
using GrowthInspector.Orchestration;
using Microsoft.Data.Sqlite;
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
            options.AgentItemTimeoutSeconds = 2;
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

/// <summary>可手动推进的测试时钟。</summary>
internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}
