using GrowthInspector.Core.Abstractions;
using GrowthInspector.Notifications.Lark;
using Microsoft.Extensions.DependencyInjection;

namespace GrowthInspector.Notifications;

public static class NotificationsServiceCollectionExtensions
{
    /// <summary>注册 Lark 通知通道（typed HttpClient，超时由 HttpClient 默认值兜底）。</summary>
    public static IServiceCollection AddGrowthInspectorNotifications(this IServiceCollection services)
    {
        services.AddHttpClient<ILarkNotifier, LarkNotifier>();
        return services;
    }
}
