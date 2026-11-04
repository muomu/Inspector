using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Core.Options;
using GrowthInspector.Data;
using GrowthInspector.Management;
using GrowthInspector.Orchestration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GrowthInspector.Tests.Management;

/// <summary>管理 service 单测上下文：临时 SQLite + 真实 DI 注册，结束后删库。</summary>
internal sealed class ManagementTestContext : IAsyncDisposable
{
    private readonly string databasePath;
    private readonly ServiceProvider serviceProvider;

    private ManagementTestContext(string databasePath, ServiceProvider serviceProvider)
    {
        this.databasePath = databasePath;
        this.serviceProvider = serviceProvider;
    }

    public IManagementService Management => serviceProvider.GetRequiredService<IManagementService>();

    public IRunbookWriteService RunbookWriter => serviceProvider.GetRequiredService<IRunbookWriteService>();

    public IDbContextFactory<GrowthInspectorDbContext> DbFactory
        => serviceProvider.GetRequiredService<IDbContextFactory<GrowthInspectorDbContext>>();

    public static async Task<ManagementTestContext> CreateAsync(
        Action<IServiceCollection>? configureServices = null)
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"growth-inspector-management-test-{Guid.NewGuid():N}.db");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ManagementMcp:Enabled"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.Configure<InspectionOptions>(_ => { });
        services.AddGrowthInspectorData(databasePath);
        services.AddGrowthInspectorOrchestration();
        configureServices?.Invoke(services);
        services.AddGrowthInspectorManagement(configuration);

        var provider = services.BuildServiceProvider();
        await provider.EnsureGrowthInspectorDatabaseAsync();
        return new ManagementTestContext(databasePath, provider);
    }

    public async Task<Project> SeedProjectAsync(string name, string? webhook = null)
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        var project = new Project { Name = name, Code = name.ToLowerInvariant(), LarkWebhookUrl = webhook };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project;
    }

    public async Task<GrowthInspector.Core.Models.Environment> SeedEnvironmentAsync(Guid projectId, string name)
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        var environment = new GrowthInspector.Core.Models.Environment { ProjectId = projectId, Name = name };
        db.Environments.Add(environment);
        await db.SaveChangesAsync();
        return environment;
    }

    public async ValueTask DisposeAsync()
    {
        await serviceProvider.DisposeAsync();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = databasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}

/// <summary>记录触发请求的假队列，供“触发运行只入队不执行”的断言使用。</summary>
internal sealed class RecordingChecklistRunQueue : IChecklistRunQueue
{
    public List<(ChecklistTrigger Trigger, Guid ChecklistId, Guid EnvironmentId)> Enqueued { get; } = [];

    public ValueTask EnqueueAsync(
        ChecklistTrigger trigger,
        Guid checklistId,
        Guid environmentId,
        CancellationToken cancellationToken = default)
    {
        Enqueued.Add((trigger, checklistId, environmentId));
        return ValueTask.CompletedTask;
    }
}
