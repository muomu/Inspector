using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Options;
using GrowthInspector.Notifications.Findings;
using GrowthInspector.Notifications.Lark;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GrowthInspector.Notifications;

public static class NotificationsServiceCollectionExtensions
{
    /// <summary>注册 Lark 通知通道、finding 状态机与日报后台服务。</summary>
    public static IServiceCollection AddGrowthInspectorNotifications(this IServiceCollection services)
    {
        services.AddHttpClient<ILarkNotifier, LarkNotifier>();

        // v1 全链路 mock：默认 MockMode = true，只写 LarkNotification 留痕，不发起真实请求。
        // 时钟以 TimeProvider 注入，测试可替换为固定时钟。
        services.AddOptions<LarkNotificationOptions>();
        services.AddOptions<InspectionOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ILarkNotificationDispatcher, LarkNotificationDispatcher>();

        // finding 状态机（TPG-2713）：调用方在一轮执行结束后触发 EvaluateRunAsync。
        services.AddScoped<IFindingNotifier, FindingNotifier>();

        return services;
    }
}
