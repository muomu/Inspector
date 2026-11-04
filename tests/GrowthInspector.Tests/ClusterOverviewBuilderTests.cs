using System.Text.Json;
using GrowthInspector.Core.Models;
using GrowthInspector.Web.Services;

namespace GrowthInspector.Tests;

/// <summary>
/// 集群网格映射：缺失结果、部分执行、未配置严重级都不能被算成健康。
/// 这些边界用纯函数测试锁住，不依赖数据库或 Blazor 渲染。
/// </summary>
public sealed class ClusterOverviewBuilderTests
{
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Missing_node_result_is_pending_and_blocks_complete()
    {
        var serviceA = Item("service-a 进程心跳", 1);
        var serviceB = Item("service-b 进程心跳", 2);
        var mysql = Item("MySQL 连通性", 3);
        var run = Run(
            [serviceA, serviceB, mysql],
            Result(serviceA.ItemId, CheckResultStatus.Ok),
            Result(mysql.ItemId, CheckResultStatus.Ok));

        var overview = ClusterOverviewBuilder.Build(run);

        Assert.Equal(3, overview.TotalNodes);
        Assert.Equal(2, overview.OkCount);
        Assert.Equal(1, overview.PendingCount);
        Assert.False(overview.AllExpectedResultsPresent);
        var missing = Assert.Single(overview.Services, node => node.Name == "service-b");
        Assert.Equal(StatusState.Pending, missing.Status.State);
        Assert.Equal("无结果", missing.Status.Sub);
        Assert.Equal("待证 · 无结果", missing.Meta);
        Assert.Single(overview.Dependencies);
    }

    [Fact]
    public void Empty_snapshot_falls_back_to_results_but_is_not_complete()
    {
        var serviceA = Item("service-a 进程心跳", 1);
        var serviceB = Item("service-b 进程心跳", 2);
        var run = Run([], Result(serviceA.ItemId, CheckResultStatus.Ok), Result(serviceB.ItemId, CheckResultStatus.Ok));
        run.SnapshotJson = "{}";
        var titles = new Dictionary<Guid, string>
        {
            [serviceA.ItemId] = serviceA.Title,
            [serviceB.ItemId] = serviceB.Title,
        };
        foreach (var result in run.Results)
        {
            result.ChecklistItem = CurrentItem(result.ChecklistItemId, titles[result.ChecklistItemId]);
        }

        var overview = ClusterOverviewBuilder.Build(run);

        Assert.Equal(2, overview.TotalNodes);
        Assert.Equal(2, overview.OkCount);
        Assert.Equal(2, overview.ExpectedItemCount);
        Assert.False(overview.AllExpectedResultsPresent);
    }

    [Fact]
    public void Abnormal_without_severity_is_pending_not_neutral()
    {
        var service = Item("service-a 进程心跳", 1, severity: null);
        var run = Run([service], Result(service.ItemId, CheckResultStatus.Abnormal));

        var overview = ClusterOverviewBuilder.Build(run);
        var node = Assert.Single(overview.Services);

        Assert.Equal(StatusState.Pending, node.Status.State);
        Assert.Equal(0, overview.NeutralCount);
        Assert.Equal(1, overview.PendingCount);
    }

    [Fact]
    public void Abnormal_info_severity_stays_neutral()
    {
        var service = Item("service-a 进程心跳", 1, severity: Severity.Info);
        var run = Run([service], Result(service.ItemId, CheckResultStatus.Abnormal));

        var overview = ClusterOverviewBuilder.Build(run);

        Assert.Equal(1, overview.NeutralCount);
        Assert.Equal(0, overview.PendingCount);
    }

    [Fact]
    public void Non_node_abnormal_counts_as_other_attention()
    {
        var lag = Item("Kafka 原生消费组 lag", 1, severity: Severity.Warning);
        var run = Run([lag], Result(lag.ItemId, CheckResultStatus.Abnormal));

        var overview = ClusterOverviewBuilder.Build(run);

        Assert.Equal(0, overview.TotalNodes);
        Assert.Equal(1, overview.OtherAttentionCount);
        Assert.Equal(0, overview.OtherPendingCount);
    }

    [Fact]
    public void Missing_pipeline_result_counts_pending_and_blocks_complete()
    {
        var service = Item("service-a 进程心跳", 1);
        var lag = Item("Kafka 原生消费组 lag", 2);
        var run = Run([service, lag], Result(service.ItemId, CheckResultStatus.Ok));

        var overview = ClusterOverviewBuilder.Build(run);

        Assert.Equal(1, overview.OtherPendingCount);
        Assert.False(overview.AllExpectedResultsPresent);
    }

    [Fact]
    public void Snapshot_severity_wins_over_current_item_edits()
    {
        var service = Item("service-a 进程心跳", 1, severity: Severity.Critical);
        var run = Run([service], Result(service.ItemId, CheckResultStatus.Abnormal));
        run.Results.Single().ChecklistItem = new ChecklistItem
        {
            Id = service.ItemId,
            Title = service.Title,
            Severity = Severity.Warning,
        };

        var overview = ClusterOverviewBuilder.Build(run);

        Assert.Equal(StatusState.Danger, Assert.Single(overview.Services).Status.State);
    }

    [Fact]
    public void Escaped_snapshot_parses_and_contains_cluster_items()
    {
        var service = Item("service-a 进程心跳", 1);
        var run = Run([service], Result(service.ItemId, CheckResultStatus.Ok));

        Assert.Matches(@"\\u8[fF][dD][bB]", run.SnapshotJson);
        Assert.True(ClusterOverviewBuilder.ContainsClusterItems(run.SnapshotJson));
    }

    [Fact]
    public void Invalid_snapshot_does_not_throw_and_falls_back_to_results()
    {
        var service = Item("service-a 进程心跳", 1);
        var run = Run([service], Result(service.ItemId, CheckResultStatus.Ok));
        run.SnapshotJson = "{";
        run.Results.Single().ChecklistItem = CurrentItem(service.ItemId, service.Title);

        var overview = ClusterOverviewBuilder.Build(run);

        Assert.Equal(StatusState.Ok, Assert.Single(overview.Services).Status.State);
        Assert.False(overview.AllExpectedResultsPresent);
    }

    private static ChecklistSnapshotItem Item(
        string title,
        int sortOrder,
        ComparisonOperator? operation = ComparisonOperator.Eq,
        Severity? severity = Severity.Critical)
        => new(
            Guid.NewGuid(),
            title,
            sortOrder,
            "mock__GetServiceHealth",
            "{\"service\":\"service-a\"}",
            "$.status",
            operation,
            "healthy",
            "5m",
            severity,
            1,
            "mock",
            "healthy");

    private static ChecklistRun Run(
        IReadOnlyList<ChecklistSnapshotItem> items,
        params CheckResult[] results)
    {
        var snapshot = new ChecklistSnapshot(
            Guid.NewGuid(),
            "运行健康巡检（Growth）",
            ChecklistFrequency.Hourly,
            ChecklistTrigger.Manual,
            items,
            [],
            [],
            []);

        var run = new ChecklistRun
        {
            SnapshotJson = JsonSerializer.Serialize(snapshot, SnapshotJsonOptions),
            Status = RunStatus.Succeeded,
        };

        foreach (var result in results)
        {
            run.Results.Add(result);
        }

        return run;
    }

    private static CheckResult Result(
        Guid itemId,
        CheckResultStatus status,
        string? recomputedValue = null,
        string? error = null)
        => new()
        {
            ChecklistItemId = itemId,
            Status = status,
            RecomputedValue = recomputedValue,
            ErrorMessage = error,
        };

    private static ChecklistItem CurrentItem(Guid itemId, string title)
        => new()
        {
            Id = itemId,
            Title = title,
            SortOrder = 1,
            ToolName = "mock__GetServiceHealth",
            ValuePath = "$.status",
            Operator = ComparisonOperator.Eq,
            Threshold = "healthy",
            Severity = Severity.Critical,
            RuleVersion = 1,
        };
}
