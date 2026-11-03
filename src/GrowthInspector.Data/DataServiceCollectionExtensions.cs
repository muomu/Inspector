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

    /// <summary>确保 SQLite 库与 schema 存在（EnsureCreated，不做迁移）。</summary>
    public static async Task EnsureGrowthInspectorDatabaseAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<GrowthInspectorDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
    }
}
