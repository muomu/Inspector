using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Options;
using GrowthInspector.Data;
using GrowthInspector.Orchestration.Queue;
using GrowthInspector.Orchestration.Scheduling;
using GrowthInspector.Orchestration.Summaries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Orchestration;

/// <summary>编排层 DI 注册：后台队列、调度扫描、执行编排与摘要服务。</summary>
public static class OrchestrationServiceCollectionExtensions
{
    public static IServiceCollection AddGrowthInspectorOrchestration(this IServiceCollection services)
    {
        services.AddOptions();
        services.TryAddSingleton(TimeProvider.System);

        // TPG-2706：创建执行（快照 + 互斥）、进程内队列、后台 worker、启动清理、频率扫描。
        services.AddSingleton<ChecklistRunFactory>();
        services.AddSingleton<ChecklistRunQueue>();
        services.AddSingleton<IChecklistRunQueue>(sp => sp.GetRequiredService<ChecklistRunQueue>());
        services.AddSingleton<ChecklistRunRecovery>();
        services.AddSingleton<ChecklistRunStartupGate>();
        services.AddHostedService<ChecklistRunRecoveryHostedService>();
        services.AddHostedService<ChecklistRunQueueWorker>();
        services.AddSingleton<ChecklistScheduleScanner>();

        // TPG-2707：执行编排（检查单内串行、检查单间并行），同时保留 IInspectionOrchestrator 兼容入口。
        services.AddScoped<ChecklistRunOrchestrator>();
        services.AddScoped<IChecklistRunExecutor>(sp => sp.GetRequiredService<ChecklistRunOrchestrator>());
        services.AddScoped<IInspectionOrchestrator>(sp => sp.GetRequiredService<ChecklistRunOrchestrator>());

        // TPG-2711：摘要 agent。IChatClient 可选：未注册（或未配置密钥）时摘要服务走模板兜底。
        services.AddScoped<IRunSummaryService>(sp => new RunSummaryService(
            sp.GetRequiredService<IDbContextFactory<GrowthInspectorDbContext>>(),
            sp.GetService<IChatClient>(),
            sp.GetRequiredService<IOptions<DeepSeekOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<RunSummaryService>>()));

        return services;
    }
}
