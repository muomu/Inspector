using GrowthInspector.Core.Models;
using GrowthInspector.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GrowthInspector.Seeder;

/// <summary>
/// seed 导入入口：EnsureCreated 建库 / 建表，再把 seed 目录按业务键幂等写入。
/// 只写本地 SQLite，不联网、不调用任何真实数据源。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        SeederArguments arguments;
        try
        {
            arguments = SeederArguments.Parse(args);
        }
        catch (SeederUsageException ex)
        {
            Console.Error.WriteLine("参数错误：" + ex.Message);
            PrintUsage();
            return 2;
        }

        if (arguments.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        NormalizedSeed seed;
        try
        {
            seed = await SeedLoader.LoadAsync(arguments.SeedDirectory);
        }
        catch (SeedValidationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        Console.WriteLine("Growth Inspector 种子导入");
        Console.WriteLine($"  seed 目录：{arguments.SeedDirectory}");
        Console.WriteLine($"  数据库：  {arguments.DatabasePath}");
        Console.WriteLine();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(arguments.DatabasePath)!);

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = arguments.DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
            }.ToString();

            var options = new DbContextOptionsBuilder<GrowthInspectorDbContext>()
                .UseSqlite(connectionString)
                .Options;

            await using var db = new GrowthInspectorDbContext(options);
            var created = await db.Database.EnsureCreatedAsync();
            Console.WriteLine(created ? "数据库不存在，已按当前模型建库建表。" : "数据库已存在，沿用既有表结构。");
            Console.WriteLine();

            var summary = await new SeedImporter(db).ImportAsync(seed);
            PrintSummary(seed, summary);
            Console.WriteLine();
            Console.WriteLine("导入完成。");
            return 0;
        }
        catch (Exception ex) when (ex is DbUpdateException or SqliteException)
        {
            Console.Error.WriteLine("写库失败：" + ex.Message);
            Console.Error.WriteLine("提示：EnsureCreated 不做迁移，模型变更后请删除目标库文件重跑。");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("导入失败：" + ex);
            return 1;
        }
    }

    private static void PrintSummary(NormalizedSeed seed, ImportSummary summary)
    {
        Console.WriteLine("写入结果（新增 / 更新 / 归档）：");
        foreach (var (entity, change) in summary.Changes)
        {
            Console.WriteLine($"  {entity,-20} {change.Added,3} / {change.Updated,3} / {change.Archived,3}");
        }

        Console.WriteLine($"  {"合计",-20} {summary.TotalAdded,3} / {summary.TotalUpdated,3} / {summary.TotalArchived,3}");
        Console.WriteLine();

        var general = seed.Runbooks.Count(x => x.Kind == RunbookKind.General);
        var specialized = seed.Runbooks.Count(x => x.Kind == RunbookKind.Specialized);
        Console.WriteLine($"手册：{seed.Runbooks.Count} 份（General {general} / Specialized {specialized}）");

        var items = seed.Checklists.SelectMany(x => x.Items).ToList();
        Console.WriteLine($"检查单：{seed.Checklists.Count} 张；检查项 {items.Count} 个（自然语言描述 + 严重级别）");

        foreach (var checklist in seed.Checklists)
        {
            Console.WriteLine(
                $"  - {checklist.Name}（{checklist.Frequency}）："
                + $"{checklist.Items.Count} 项，引用手册 {checklist.RunbookTitles.Count} 份");
        }

        Console.WriteLine($"MCP server：{seed.McpServers.Count} 个；环境：{seed.Environments.Count} 个");
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            用法：
              dotnet run --project tools/GrowthInspector.Seeder -- --db <sqlite 路径> [--seed <seed 目录>]

            参数：
              --db     目标 SQLite 文件路径（不存在则新建，父目录自动创建）
              --seed   seed 目录，默认为 seed/growth
              -h, --help   显示本帮助

            导入语义：
              EnsureCreated 建库建表后，按 Project.Code / Environment.Name / McpServer.Name /
              Runbook.Title / Checklist.Name / ChecklistItem.Title 判断新增或更新，可重复执行。
            """);
    }
}
