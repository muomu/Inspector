using GrowthInspector.Core.Models;
using GrowthInspector.Data;
using Microsoft.EntityFrameworkCore;
using Environment = GrowthInspector.Core.Models.Environment;

namespace GrowthInspector.Seeder;

/// <summary>一次导入的计数：按实体名累计新增 / 更新 / 归档。</summary>
internal sealed class ImportSummary
{
    private readonly Dictionary<string, EntityChange> _changes = new(StringComparer.Ordinal);

    public void Added(string entity) => Get(entity).Added++;

    public void Updated(string entity) => Get(entity).Updated++;

    public void Archived(string entity) => Get(entity).Archived++;

    public IReadOnlyDictionary<string, EntityChange> Changes => _changes;

    public int TotalAdded => _changes.Values.Sum(x => x.Added);

    public int TotalUpdated => _changes.Values.Sum(x => x.Updated);

    public int TotalArchived => _changes.Values.Sum(x => x.Archived);

    private EntityChange Get(string entity)
    {
        if (!_changes.TryGetValue(entity, out var change))
        {
            change = new EntityChange();
            _changes[entity] = change;
        }

        return change;
    }

    internal sealed class EntityChange
    {
        public int Added { get; set; }

        public int Updated { get; set; }

        public int Archived { get; set; }
    }
}

/// <summary>
/// 幂等导入器：按业务键（Project.Code / Environment.Name / McpServer.Name / Runbook.Title /
/// Checklist.Name / ChecklistItem.Title）判断新增还是更新，整批导入在一个事务里完成。
/// 不删除 seed 之外的数据，只把 seed 内检查单里已不在 seed 中的检查项标为归档。
/// </summary>
internal sealed class SeedImporter(GrowthInspectorDbContext db)
{
    /// <summary>临时 SortOrder 的基数偏移：先把既有项挪开，再落到目标顺序，避免唯一索引冲突。</summary>
    private const int SortOrderTemporaryBase = 1_000_000;

    public async Task<ImportSummary> ImportAsync(NormalizedSeed seed, CancellationToken cancellationToken = default)
    {
        var summary = new ImportSummary();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var project = await UpsertProjectAsync(seed.Project, summary, cancellationToken);
        var environments = await UpsertEnvironmentsAsync(project, seed.Environments, summary, cancellationToken);
        await UpsertMcpServersAsync(project, environments, seed.McpServers, summary, cancellationToken);
        var runbooks = await UpsertRunbooksAsync(project, seed.Runbooks, summary, cancellationToken);
        await UpsertChecklistsAsync(project, runbooks, seed.Checklists, summary, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return summary;
    }

    private async Task<Project> UpsertProjectAsync(ProjectSpec spec, ImportSummary summary, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(x => x.Code == spec.Code, cancellationToken);
        if (project is null)
        {
            project = new Project { Code = spec.Code };
            db.Projects.Add(project);
            summary.Added(nameof(Project));
        }
        else
        {
            summary.Updated(nameof(Project));
        }

        project.Name = spec.Name;
        project.LarkWebhookUrl = spec.LarkWebhookUrl;
        project.OpsContext = spec.OpsContext;
        project.IsArchived = false;

        await db.SaveChangesAsync(cancellationToken);
        return project;
    }

    private async Task<Dictionary<string, Environment>> UpsertEnvironmentsAsync(
        Project project,
        IReadOnlyList<EnvironmentSpec> specs,
        ImportSummary summary,
        CancellationToken cancellationToken)
    {
        var existing = await db.Environments
            .Where(x => x.ProjectId == project.Id)
            .ToListAsync(cancellationToken);
        var byName = existing.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var result = new Dictionary<string, Environment>(StringComparer.Ordinal);

        foreach (var spec in specs)
        {
            if (!byName.TryGetValue(spec.Name, out var environment))
            {
                environment = new Environment { ProjectId = project.Id, Name = spec.Name };
                db.Environments.Add(environment);
                summary.Added(nameof(Environment));
            }
            else
            {
                summary.Updated(nameof(Environment));
            }

            environment.IsDefault = spec.IsDefault;
            environment.IsArchived = false;
            result[spec.Name] = environment;
        }

        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    private async Task UpsertMcpServersAsync(
        Project project,
        IReadOnlyDictionary<string, Environment> environments,
        IReadOnlyList<McpServerSpec> specs,
        ImportSummary summary,
        CancellationToken cancellationToken)
    {
        var existing = await db.McpServers
            .Where(x => x.ProjectId == project.Id)
            .ToListAsync(cancellationToken);
        var byName = existing.ToDictionary(x => x.Name, StringComparer.Ordinal);

        foreach (var spec in specs)
        {
            if (!byName.TryGetValue(spec.Name, out var server))
            {
                server = new McpServer { ProjectId = project.Id, Name = spec.Name };
                db.McpServers.Add(server);
                summary.Added(nameof(McpServer));
            }
            else
            {
                summary.Updated(nameof(McpServer));
            }

            server.EnvironmentId = spec.EnvironmentName is null ? null : environments[spec.EnvironmentName].Id;
            server.Transport = spec.Transport;
            server.Command = spec.Command;
            server.ArgumentsJson = spec.ArgumentsJson;
            server.Url = spec.Url;
            server.IsEnabled = spec.IsEnabled;
            server.IsArchived = false;
            server.TimeoutSeconds = spec.TimeoutSeconds;
            server.MaxResultBytes = spec.MaxResultBytes;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Dictionary<string, Runbook>> UpsertRunbooksAsync(
        Project project,
        IReadOnlyList<RunbookSpec> specs,
        ImportSummary summary,
        CancellationToken cancellationToken)
    {
        var existing = await db.Runbooks
            .Where(x => x.ProjectId == project.Id)
            .ToListAsync(cancellationToken);
        var byTitle = existing.ToDictionary(x => x.Title, StringComparer.Ordinal);
        var result = new Dictionary<string, Runbook>(StringComparer.Ordinal);

        foreach (var spec in specs)
        {
            if (!byTitle.TryGetValue(spec.Title, out var runbook))
            {
                runbook = new Runbook { ProjectId = project.Id, Title = spec.Title };
                db.Runbooks.Add(runbook);
                summary.Added(nameof(Runbook));
            }
            else
            {
                summary.Updated(nameof(Runbook));
            }

            runbook.Kind = spec.Kind;
            runbook.Body = spec.Body;
            runbook.IsArchived = false;
            result[spec.Title] = runbook;
        }

        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    private async Task UpsertChecklistsAsync(
        Project project,
        IReadOnlyDictionary<string, Runbook> runbooks,
        IReadOnlyList<ChecklistSpec> specs,
        ImportSummary summary,
        CancellationToken cancellationToken)
    {
        var existing = await db.Checklists
            .Where(x => x.ProjectId == project.Id)
            .ToListAsync(cancellationToken);
        var byName = existing.ToDictionary(x => x.Name, StringComparer.Ordinal);

        foreach (var spec in specs)
        {
            if (!byName.TryGetValue(spec.Name, out var checklist))
            {
                checklist = new Checklist { ProjectId = project.Id, Name = spec.Name };
                db.Checklists.Add(checklist);
                summary.Added(nameof(Checklist));
            }
            else
            {
                summary.Updated(nameof(Checklist));
            }

            checklist.Description = spec.Description;
            checklist.Frequency = spec.Frequency;
            checklist.IsEnabled = spec.IsEnabled;
            checklist.IsArchived = false;
            await db.SaveChangesAsync(cancellationToken);

            await UpsertItemsAsync(checklist, spec, summary, cancellationToken);
            await UpsertRunbookRefsAsync(checklist, spec, runbooks, summary, cancellationToken);
        }
    }

    private async Task UpsertItemsAsync(
        Checklist checklist,
        ChecklistSpec spec,
        ImportSummary summary,
        CancellationToken cancellationToken)
    {
        var existing = await db.ChecklistItems
            .Where(x => x.ChecklistId == checklist.Id)
            .ToListAsync(cancellationToken);
        var byTitle = existing.ToDictionary(x => x.Title, StringComparer.Ordinal);

        // 第一阶段：全部落到临时 SortOrder，规避 (ChecklistId, SortOrder) 唯一索引在换序时的中间冲突。
        var items = new List<(ChecklistItem Entity, ChecklistItemSpec Spec)>();
        for (var i = 0; i < spec.Items.Count; i++)
        {
            var itemSpec = spec.Items[i];
            if (!byTitle.TryGetValue(itemSpec.Title, out var item))
            {
                item = new ChecklistItem { ChecklistId = checklist.Id, Title = itemSpec.Title };
                db.ChecklistItems.Add(item);
                summary.Added(nameof(ChecklistItem));
            }
            else
            {
                summary.Updated(nameof(ChecklistItem));
            }

            ApplyItemFields(item, itemSpec);
            item.IsArchived = false;
            item.SortOrder = SortOrderTemporaryBase + i;
            items.Add((item, itemSpec));
        }

        // seed 里已经没有的检查项：归档而不是硬删（历史 CheckResult / Finding 通过外键引用它）。
        foreach (var stale in existing.Where(x => spec.Items.All(s => !string.Equals(s.Title, x.Title, StringComparison.Ordinal))))
        {
            if (!stale.IsArchived)
            {
                stale.IsArchived = true;
                summary.Archived(nameof(ChecklistItem));
            }

            stale.SortOrder = SortOrderTemporaryBase + 100_000 + stale.SortOrder;
        }

        await db.SaveChangesAsync(cancellationToken);

        // 第二阶段：落到目标 SortOrder。
        foreach (var (entity, itemSpec) in items)
        {
            entity.SortOrder = itemSpec.SortOrder;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static void ApplyItemFields(ChecklistItem item, ChecklistItemSpec spec)
    {
        item.Title = spec.Title;
        item.Body = spec.Body;
        item.Severity = spec.Severity;
    }

    private async Task UpsertRunbookRefsAsync(
        Checklist checklist,
        ChecklistSpec spec,
        IReadOnlyDictionary<string, Runbook> runbooks,
        ImportSummary summary,
        CancellationToken cancellationToken)
    {
        var targetIds = spec.RunbookTitles.Select(title => runbooks[title].Id).ToHashSet();

        var existing = await db.ChecklistRunbookRefs
            .Where(x => x.ChecklistId == checklist.Id)
            .ToListAsync(cancellationToken);

        foreach (var stale in existing.Where(x => !targetIds.Contains(x.RunbookId)))
        {
            db.ChecklistRunbookRefs.Remove(stale);
            summary.Archived("ChecklistRunbookRef");
        }

        var existingIds = existing.Select(x => x.RunbookId).ToHashSet();
        foreach (var runbookId in targetIds.Where(id => !existingIds.Contains(id)))
        {
            db.ChecklistRunbookRefs.Add(new ChecklistRunbookRef
            {
                ChecklistId = checklist.Id,
                RunbookId = runbookId,
            });
            summary.Added("ChecklistRunbookRef");
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
