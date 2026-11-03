using System.Text.Json;
using GrowthInspector.Agents;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Core.Options;
using GrowthInspector.Data;
using GrowthInspector.Orchestration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Environment = GrowthInspector.Core.Models.Environment;

namespace GrowthInspector.Tests;

/// <summary>骨架冒烟测试：建库、落库、编排入口可用。真实业务单测按子任务补充。</summary>
public sealed class SkeletonSmokeTests : IDisposable
{
    private readonly string databasePath =
        Path.Combine(Path.GetTempPath(), $"growth-inspector-test-{Guid.NewGuid():N}.db");

    private readonly ServiceProvider serviceProvider;

    public SkeletonSmokeTests()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.Configure<InspectionOptions>(_ => { });
        services.AddGrowthInspectorData(databasePath);
        services.AddGrowthInspectorAgents(new DeepSeekOptions());
        services.AddGrowthInspectorOrchestration();
        serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task EnsureCreated_CreatesSchema_AndPersistsProject()
    {
        await serviceProvider.EnsureGrowthInspectorDatabaseAsync();

        var factory = serviceProvider.GetRequiredService<IDbContextFactory<GrowthInspectorDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        db.Projects.Add(new Project { Name = "示例项目", Code = "demo" });
        await db.SaveChangesAsync();

        Assert.Equal(1, await db.Projects.CountAsync());
    }

    [Fact]
    public async Task Orchestrator_WritesSnapshotAndResults()
    {
        await serviceProvider.EnsureGrowthInspectorDatabaseAsync();
        var factory = serviceProvider.GetRequiredService<IDbContextFactory<GrowthInspectorDbContext>>();

        Project project;
        Checklist checklist;
        await using (var db = await factory.CreateDbContextAsync())
        {
            project = new Project { Name = "示例项目", Code = "demo" };
            project.Environments.Add(new Environment { Name = "prod", IsDefault = true });

            var runbook = new Runbook
            {
                Title = "示例手册",
                Body = "# 示例手册\n\n指标语义与排查经验。",
                Project = project,
            };

            checklist = new Checklist
            {
                Name = "示例检查单",
                Frequency = ChecklistFrequency.Hourly,
                Project = project,
            };
            checklist.Items.Add(new ChecklistItem { Title = "信息型检查项", SortOrder = 0 });
            checklist.Items.Add(new ChecklistItem
            {
                Title = "判定型检查项",
                SortOrder = 1,
                Threshold = "< 500",
                Severity = Severity.Critical,
            });
            checklist.RunbookRefs.Add(new ChecklistRunbookRef { Runbook = runbook });

            db.Projects.Add(project);
            db.Checklists.Add(checklist);
            await db.SaveChangesAsync();
        }

        var environmentId = project.Environments.First().Id;
        var orchestrator = serviceProvider.GetRequiredService<IInspectionOrchestrator>();
        var runId = await orchestrator.RunAsync(checklist.Id, environmentId);

        await using var verifyDb = await factory.CreateDbContextAsync();
        var run = await verifyDb.ChecklistRuns
            .Include(x => x.Results)
            .SingleAsync(x => x.Id == runId);

        Assert.Equal(2, run.Results.Count);
        Assert.All(run.Results, result => Assert.Equal(CheckResultStatus.InsufficientEvidence, result.Status));

        var snapshot = JsonSerializer.Deserialize<ChecklistSnapshot>(
            run.SnapshotJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.Items.Count);
        Assert.Single(snapshot.Runbooks);
    }

    public void Dispose()
    {
        serviceProvider.Dispose();
        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }
    }
}
