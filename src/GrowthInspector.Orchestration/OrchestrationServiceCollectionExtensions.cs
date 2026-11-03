using GrowthInspector.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace GrowthInspector.Orchestration;

public static class OrchestrationServiceCollectionExtensions
{
    /// <summary>注册检查单执行编排（骨架实现，后续由 MAF Workflow 替换内部实现）。</summary>
    public static IServiceCollection AddGrowthInspectorOrchestration(this IServiceCollection services)
    {
        services.AddScoped<IInspectionOrchestrator, ChecklistRunOrchestrator>();
        return services;
    }
}
