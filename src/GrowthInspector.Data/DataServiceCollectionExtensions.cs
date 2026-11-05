using GrowthInspector.Core.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrowthInspector.Data;

/// <summary>Data 层 DI 注册与建库入口。</summary>
public static class DataServiceCollectionExtensions
{
    /// <summary>注册 DbContextFactory（Blazor Server 需按操作创建上下文，不能全局共享 scoped 实例）。</summary>
    public static IServiceCollection AddGrowthInspectorData(this IServiceCollection services, string sqlitePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlitePath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(sqlitePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sqlitePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();

        services.AddDbContextFactory<GrowthInspectorDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<IAuditPruner, AuditPruner>();

        return services;
    }

    /// <summary>
    /// 注册定时审计清理（BackgroundService）。宿主需额外调用本方法才会启用；
    /// 清理周期与首次延迟可通过 <paramref name="configure"/> 覆盖。
    /// </summary>
    public static IServiceCollection AddGrowthInspectorAuditPruning(
        this IServiceCollection services,
        Action<AuditPruningOptions>? configure = null)
    {
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddHostedService<AuditPruningHostedService>();
        return services;
    }

    /// <summary>确保 SQLite 库与 schema 存在（EnsureCreated，不做迁移）。</summary>
    public static async Task EnsureGrowthInspectorDatabaseAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<GrowthInspectorDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await PatchMissingColumnsAsync(db, cancellationToken);
    }

    /// <summary>
    /// EnsureCreated 不迁移存量库：为版本升级新增的列做补列（只加不改）。
    /// 失败不阻断启动；列缺失时首次读写会暴露具体问题。
    /// </summary>
    private static async Task PatchMissingColumnsAsync(GrowthInspectorDbContext db, CancellationToken cancellationToken)
    {
        (string Table, string Column, string Definition)[] patches =
        [
            ("ChecklistRuns", "CachedPromptTokens", "INTEGER NOT NULL DEFAULT 0"),
            ("CheckResults", "CachedPromptTokens", "INTEGER NOT NULL DEFAULT 0"),
        ];

        try
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
            }

            foreach (var (table, column, definition) in patches)
            {
                var exists = false;
                var checkCommand = connection.CreateCommand();
                checkCommand.CommandText = $"PRAGMA table_info({table});";
                using (var reader = await checkCommand.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                        {
                            exists = true;
                            break;
                        }
                    }
                }

                if (exists)
                {
                    continue;
                }

                var alterCommand = connection.CreateCommand();
                alterCommand.CommandText = $"ALTER TABLE {table} ADD COLUMN \"{column}\" {definition};";
                await alterCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (Exception)
        {
            // 尽力而为：补列失败不阻止应用启动。
        }
    }
}
