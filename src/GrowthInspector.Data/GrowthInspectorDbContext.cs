using GrowthInspector.Core.Models;
using GrowthInspector.Data.Converters;
using Microsoft.EntityFrameworkCore;
using Environment = GrowthInspector.Core.Models.Environment;

namespace GrowthInspector.Data;

/// <summary>巡检框架的 EF Core（SQLite）数据上下文。</summary>
public sealed class GrowthInspectorDbContext(DbContextOptions<GrowthInspectorDbContext> options)
    : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();

    public DbSet<Environment> Environments => Set<Environment>();

    public DbSet<Runbook> Runbooks => Set<Runbook>();

    public DbSet<Checklist> Checklists => Set<Checklist>();

    public DbSet<ChecklistItem> ChecklistItems => Set<ChecklistItem>();

    public DbSet<ChecklistRunbookRef> ChecklistRunbookRefs => Set<ChecklistRunbookRef>();

    public DbSet<ChecklistRun> ChecklistRuns => Set<ChecklistRun>();

    public DbSet<CheckResult> CheckResults => Set<CheckResult>();

    public DbSet<Finding> Findings => Set<Finding>();

    public DbSet<AiSummary> AiSummaries => Set<AiSummary>();

    public DbSet<ToolCallLog> ToolCallLogs => Set<ToolCallLog>();

    public DbSet<McpServer> McpServers => Set<McpServer>();

    public DbSet<LarkNotification> LarkNotifications => Set<LarkNotification>();

    public DbSet<LlmSetting> LlmSettings => Set<LlmSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureDateTimeOffsetConversions(modelBuilder);
        ConfigureProjects(modelBuilder);
        ConfigureEnvironments(modelBuilder);
        ConfigureRunbooks(modelBuilder);
        ConfigureChecklists(modelBuilder);
        ConfigureChecklistItems(modelBuilder);
        ConfigureChecklistRunbookRefs(modelBuilder);
        ConfigureChecklistRuns(modelBuilder);
        ConfigureCheckResults(modelBuilder);
        ConfigureFindings(modelBuilder);
        ConfigureAiSummaries(modelBuilder);
        ConfigureToolCallLogs(modelBuilder);
        ConfigureMcpServers(modelBuilder);
        ConfigureLarkNotifications(modelBuilder);
        ConfigureLlmSettings(modelBuilder);
    }

    private static void ConfigureDateTimeOffsetConversions(ModelBuilder modelBuilder)
    {
        // SQLite 不支持 DateTimeOffset 的 ORDER BY；统一转成 UTC ticks（long）以支持排序与比较。
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(new UtcDateTimeOffsetToTicksConverter());
                }
                else if (property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(new UtcNullableDateTimeOffsetToTicksConverter());
                }
            }
        }
    }

    private static void ConfigureProjects(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>(entity =>
        {
            entity.ToTable("Projects");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired().HasMaxLength(200);
            entity.Property(x => x.Code).HasMaxLength(50);
            entity.HasIndex(x => x.Code).IsUnique();
        });
    }

    private static void ConfigureEnvironments(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Environment>(entity =>
        {
            entity.ToTable("Environments");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired().HasMaxLength(100);
            entity.HasIndex(x => new { x.ProjectId, x.Name }).IsUnique();
            entity.HasOne(x => x.Project)
                .WithMany(x => x.Environments)
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureRunbooks(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Runbook>(entity =>
        {
            entity.ToTable("Runbooks");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Title).IsRequired().HasMaxLength(200);
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.Body).IsRequired();
            entity.HasIndex(x => new { x.ProjectId, x.Kind, x.IsArchived });
            entity.HasOne(x => x.Project)
                .WithMany(x => x.Runbooks)
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureChecklists(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Checklist>(entity =>
        {
            entity.ToTable("Checklists");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired().HasMaxLength(200);
            entity.Property(x => x.Frequency).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(x => new { x.ProjectId, x.Name }).IsUnique();
            entity.HasOne(x => x.Project)
                .WithMany(x => x.Checklists)
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureChecklistItems(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChecklistItem>(entity =>
        {
            entity.ToTable("ChecklistItems");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Title).IsRequired().HasMaxLength(300);
            entity.Property(x => x.Body).IsRequired();
            entity.Property(x => x.Severity).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(x => new { x.ChecklistId, x.SortOrder });
            entity.HasIndex(x => new { x.ChecklistId, x.IsArchived });
            entity.HasOne(x => x.Checklist)
                .WithMany(x => x.Items)
                .HasForeignKey(x => x.ChecklistId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureChecklistRunbookRefs(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChecklistRunbookRef>(entity =>
        {
            entity.ToTable("ChecklistRunbookRefs");
            entity.HasKey(x => new { x.ChecklistId, x.RunbookId });
            entity.HasOne(x => x.Checklist)
                .WithMany(x => x.RunbookRefs)
                .HasForeignKey(x => x.ChecklistId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Runbook)
                .WithMany()
                .HasForeignKey(x => x.RunbookId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureChecklistRuns(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChecklistRun>(entity =>
        {
            entity.ToTable("ChecklistRuns");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.Trigger).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.SnapshotJson).IsRequired();
            entity.HasIndex(x => new { x.ChecklistId, x.StartedAt });
            entity.HasIndex(x => new { x.Status, x.StartedAt });
            entity.HasOne(x => x.Checklist)
                .WithMany()
                .HasForeignKey(x => x.ChecklistId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Environment)
                .WithMany()
                .HasForeignKey(x => x.EnvironmentId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureCheckResults(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CheckResult>(entity =>
        {
            entity.ToTable("CheckResults");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
            entity.Property(x => x.AgentVerdict).HasConversion<string>().HasMaxLength(30);
            entity.HasIndex(x => new { x.ChecklistRunId, x.ChecklistItemId }).IsUnique();
            entity.HasOne(x => x.ChecklistRun)
                .WithMany(x => x.Results)
                .HasForeignKey(x => x.ChecklistRunId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.ChecklistItem)
                .WithMany()
                .HasForeignKey(x => x.ChecklistItemId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureFindings(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Finding>(entity =>
        {
            entity.ToTable("Findings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DedupKey).IsRequired().HasMaxLength(400);
            entity.Property(x => x.Title).IsRequired().HasMaxLength(300);
            entity.Property(x => x.Severity).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.CloseReason).HasConversion<string>().HasMaxLength(20);
            // 同一去重键最多一条 Open：规则变更 / 恢复后允许同键保留多条已关闭历史记录。
            entity.HasIndex(x => x.DedupKey).IsUnique().HasFilter("\"Status\" = 'Open'");
            entity.HasIndex(x => new { x.EnvironmentId, x.Status });
            entity.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Environment>().WithMany().HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Checklist>().WithMany().HasForeignKey(x => x.ChecklistId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ChecklistItem>().WithMany().HasForeignKey(x => x.ChecklistItemId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureAiSummaries(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AiSummary>(entity =>
        {
            entity.ToTable("AiSummaries");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.Content).IsRequired();
            entity.HasIndex(x => x.ChecklistRunId);
            entity.HasIndex(x => x.ReportDate);
            entity.HasOne(x => x.ChecklistRun)
                .WithMany()
                .HasForeignKey(x => x.ChecklistRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureToolCallLogs(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ToolCallLog>(entity =>
        {
            entity.ToTable("ToolCallLogs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ToolName).IsRequired().HasMaxLength(200);
            entity.Property(x => x.AgentRole).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(x => new { x.ChecklistRunId, x.CreatedAt });
            entity.HasIndex(x => new { x.ChecklistRunId, x.ChecklistItemId });
            entity.HasOne(x => x.ChecklistRun)
                .WithMany(x => x.ToolCalls)
                .HasForeignKey(x => x.ChecklistRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureMcpServers(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<McpServer>(entity =>
        {
            entity.ToTable("McpServers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired().HasMaxLength(200);
            entity.Property(x => x.Transport).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(x => new { x.ProjectId, x.Name }).IsUnique();
            entity.HasOne(x => x.Project)
                .WithMany(x => x.McpServers)
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Environment)
                .WithMany()
                .HasForeignKey(x => x.EnvironmentId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureLarkNotifications(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LarkNotification>(entity =>
        {
            entity.ToTable("LarkNotifications");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(30);
            entity.Property(x => x.PayloadJson).IsRequired();
            entity.HasIndex(x => new { x.ProjectId, x.CreatedAt });
            entity.HasIndex(x => new { x.Kind, x.CreatedAt });
            entity.HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.ChecklistRun)
                .WithMany()
                .HasForeignKey(x => x.ChecklistRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureLlmSettings(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LlmSetting>(entity =>
        {
            // 应用级单行配置：Id 固定为 1，不参与任何业务外键。
            entity.ToTable("LlmSettings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.BaseUrl).HasMaxLength(500);
            entity.Property(x => x.Model).HasMaxLength(200);
        });
    }
}
