using System.Text.Json;
using GrowthInspector.Core.Models;

namespace GrowthInspector.Web.Services;

/// <summary>集群网格节点的类型：服务 / 依赖。</summary>
public enum ClusterNodeKind
{
    Service,
    Dependency,
}

/// <summary>一个集群对象在最近一轮检查中的展示态。</summary>
public sealed record ClusterNodeView(
    string Name,
    string Title,
    ClusterNodeKind Kind,
    string? Meta,
    StatusPresentation Status);

/// <summary>
/// 集群对象汇总。节点集合以快照里的检查项为准（6 域 + 5 依赖），
/// 结果按 ItemId 左连接；缺失结果按「待证 · 无结果」展示，不能被漏掉后当成健康。
/// </summary>
public sealed record ClusterOverview(
    IReadOnlyList<ClusterNodeView> Services,
    IReadOnlyList<ClusterNodeView> Dependencies,
    int OkCount,
    int WarningCount,
    int DangerCount,
    int PendingCount,
    int NeutralCount,
    int OtherPendingCount,
    int OtherAttentionCount,
    int ExpectedItemCount,
    bool AllExpectedResultsPresent,
    string? FirstError)
{
    public static ClusterOverview Empty { get; } = new(
        [], [], 0, 0, 0, 0, 0, 0, 0, 0, false, null);

    public int TotalNodes => Services.Count + Dependencies.Count;

    public int AttentionCount => WarningCount + DangerCount;
}

/// <summary>
/// 把一轮执行的快照 + 结果映射为总览集群网格。纯函数，便于单测锁定
/// 「缺失结果 / 部分执行 / 未配置严重级」这些容易产生假健康的边界。
/// </summary>
public static class ClusterOverviewBuilder
{
    private const string ServiceSuffix = "域运行健康";
    private const string DependencySuffix = "依赖连通";
    private const int MaxCandidateRuns = 20;

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web);

    public static ClusterOverview Build(ChecklistRun run)
    {
        var snapshotItems = ReadSnapshotItems(run.SnapshotJson);
        var expectedItems = BuildExpectedItems(run, snapshotItems);
        var resultsByItem = run.Results
            .GroupBy(result => result.ChecklistItemId)
            .ToDictionary(group => group.Key, group => group.First());

        var services = new List<ClusterNodeView>();
        var dependencies = new List<ClusterNodeView>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var expectedIds = new HashSet<Guid>();
        var otherPending = 0;
        var otherAttention = 0;

        foreach (var item in expectedItems)
        {
            expectedIds.Add(item.ItemId);
            resultsByItem.TryGetValue(item.ItemId, out var result);
            var status = result is null
                ? StatusPresenter.MissingResult()
                : StatusPresenter.FromResult(result.Status, item.Severity);

            if (TryParseNode(item.Title, out var name, out var kind))
            {
                if (seen.Add($"{kind}:{name}"))
                {
                    AddNode(services, dependencies, name, item.Title, kind, NodeMeta(status, result), status);
                }

                continue;
            }

            if (result is null)
            {
                // 流水线检查项没有结果：同样按待证观察，不能当作通过。
                otherPending++;
                continue;
            }

            CountOther(status, ref otherPending, ref otherAttention);
        }

        // 快照缺项（历史数据 / 旧格式）时，结果里多出来的节点仍然要展示，不能静默丢掉。
        foreach (var result in resultsByItem.Values.Where(result => !expectedIds.Contains(result.ChecklistItemId)))
        {
            var title = result.ChecklistItem?.Title ?? string.Empty;
            var status = StatusPresenter.FromResult(
                result.Status,
                result.ChecklistItem?.Severity);

            if (TryParseNode(title, out var name, out var kind))
            {
                if (seen.Add($"{kind}:{name}"))
                {
                    AddNode(services, dependencies, name, title, kind, NodeMeta(status, result), status);
                }

                continue;
            }

            CountOther(status, ref otherPending, ref otherAttention);
        }

        var nodes = services.Concat(dependencies).ToList();
        var expectedCount = expectedItems.Count > 0 ? expectedItems.Count : resultsByItem.Count;
        var allPresent = snapshotItems.Count > 0
            && expectedItems.Count > 0
            && expectedItems.All(item => resultsByItem.ContainsKey(item.ItemId));

        return new ClusterOverview(
            services,
            dependencies,
            nodes.Count(node => node.Status.State == StatusState.Ok),
            nodes.Count(node => node.Status.State == StatusState.Warning),
            nodes.Count(node => node.Status.State == StatusState.Danger),
            nodes.Count(node => node.Status.State == StatusState.Pending),
            nodes.Count(node => node.Status.State == StatusState.Neutral),
            otherPending,
            otherAttention,
            expectedCount,
            allPresent,
            run.Results.FirstOrDefault(result => !string.IsNullOrWhiteSpace(result.ErrorMessage))?.ErrorMessage);
    }

    /// <summary>最近一轮是否包含集群节点检查项：用于从候选执行里挑出总览要展示的那轮。</summary>
    public static bool ContainsClusterItems(string snapshotJson)
        => ReadSnapshotItems(snapshotJson).Values.Any(item => IsNodeTitle(item.Title));

    /// <summary>候选执行按 StartedAt 倒序取，命中快照里含节点检查项的第一轮。</summary>
    public static int TakeCandidateCount => MaxCandidateRuns;

    private static Dictionary<Guid, ChecklistSnapshotItem> ReadSnapshotItems(string snapshotJson)
    {
        var items = new Dictionary<Guid, ChecklistSnapshotItem>();
        try
        {
            var snapshot = JsonSerializer.Deserialize<ChecklistSnapshot>(snapshotJson, SnapshotJsonOptions);
            if (snapshot?.Items is null)
            {
                return items;
            }

            foreach (var item in snapshot.Items)
            {
                if (item is not null)
                {
                    items[item.ItemId] = item;
                }
            }
        }
        catch (JsonException)
        {
            // 快照损坏时退回当前检查项；页面仍按真实结果展示。
        }

        return items;
    }

    private static List<ChecklistSnapshotItem> BuildExpectedItems(
        ChecklistRun run,
        IReadOnlyDictionary<Guid, ChecklistSnapshotItem> snapshotItems)
    {
        if (snapshotItems.Count > 0)
        {
            return snapshotItems.Values.OrderBy(item => item.SortOrder).ToList();
        }

        return run.Results
            .Select(result => result.ChecklistItem)
            .Where(item => item is not null)
            .Select(item => new ChecklistSnapshotItem(
                item!.Id,
                item.Title,
                item.SortOrder,
                item.Body,
                item.Severity))
            .GroupBy(item => item.ItemId)
            .Select(group => group.First())
            .OrderBy(item => item.SortOrder)
            .ToList();
    }

    private static void CountOther(StatusPresentation status, ref int otherPending, ref int otherAttention)
    {
        if (status.State == StatusState.Pending)
        {
            otherPending++;
        }
        else if (status.State is StatusState.Warning or StatusState.Danger)
        {
            otherAttention++;
        }
    }

    private static void AddNode(
        List<ClusterNodeView> services,
        List<ClusterNodeView> dependencies,
        string name,
        string title,
        ClusterNodeKind kind,
        string? meta,
        StatusPresentation status)
    {
        var node = new ClusterNodeView(name, title, kind, meta, status);
        if (kind == ClusterNodeKind.Service)
        {
            services.Add(node);
        }
        else
        {
            dependencies.Add(node);
        }
    }

    private static string? NodeMeta(StatusPresentation status, CheckResult? result)
    {
        if (result is null)
        {
            return string.IsNullOrWhiteSpace(status.Sub) ? status.Label : $"{status.Label} · {status.Sub}";
        }

        return status.State switch
        {
            StatusState.Ok => null,
            StatusState.Warning or StatusState.Danger => status.Sub,
            _ => string.IsNullOrWhiteSpace(status.Sub) ? status.Label : $"{status.Label} · {status.Sub}",
        };
    }

    private static bool IsNodeTitle(string? title)
        => TryParseNode(title, out _, out _);

    private static bool TryParseNode(string? title, out string name, out ClusterNodeKind kind)
    {
        name = string.Empty;
        kind = ClusterNodeKind.Service;
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        if (title.EndsWith(ServiceSuffix, StringComparison.Ordinal))
        {
            name = title[..^ServiceSuffix.Length].Trim();
            kind = ClusterNodeKind.Service;
            return name.Length > 0;
        }

        if (title.EndsWith(DependencySuffix, StringComparison.Ordinal))
        {
            name = title[..^DependencySuffix.Length].Trim();
            kind = ClusterNodeKind.Dependency;
            return name.Length > 0;
        }

        return false;
    }
}
