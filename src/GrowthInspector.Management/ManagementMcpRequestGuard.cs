using System.Net;
using System.Security.Cryptography;
using System.Text;
using GrowthInspector.Core.Options;
using Microsoft.AspNetCore.Http;

namespace GrowthInspector.Management;

/// <summary>
/// 管理 MCP 的请求校验：Origin / Host 必须在白名单内（否则 403），bearer token 必须匹配
/// 环境变量（缺失或错误一律 401，fail-closed）。刻意不依赖 ASP.NET Core 认证栈，保持零配置。
/// </summary>
public static class ManagementMcpRequestGuard
{
    /// <summary>
    /// 默认 BindAddress 为 loopback，此时只接受来自 loopback 的客户端；
    /// 配置成非 loopback 地址时交给宿主绑定策略，不在这里额外限制。
    /// </summary>
    public static bool IsAllowedClientAddress(HttpContext context, ManagementMcpOptions options)
    {
        if (!IPAddress.TryParse(options.BindAddress, out var bindAddress) || !IPAddress.IsLoopback(bindAddress))
        {
            return true;
        }

        var remoteAddress = context.Connection.RemoteIpAddress;
        if (remoteAddress is null)
        {
            return false;
        }

        if (IPAddress.IsLoopback(remoteAddress))
        {
            return true;
        }

        return remoteAddress.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(remoteAddress.MapToIPv4());
    }

    public static bool IsAllowedHostAndOrigin(HttpRequest request, ManagementMcpOptions options)
    {
        var allowedOrigins = NormalizeOrigins(options.AllowedOrigins);
        if (allowedOrigins.Count == 0)
        {
            return false;
        }

        var hostValue = request.Host.Value;
        if (string.IsNullOrWhiteSpace(hostValue))
        {
            return false;
        }

        var allowedHosts = allowedOrigins
            .Select(origin => origin.Authority)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!allowedHosts.Contains(hostValue))
        {
            return false;
        }

        var originHeader = request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(originHeader))
        {
            return true;
        }

        var normalizedOrigin = NormalizeOrigin(originHeader);
        return normalizedOrigin is not null && allowedOrigins.Contains(normalizedOrigin);
    }

    public static bool HasValidToken(HttpRequest request, ManagementMcpOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.TokenEnvironmentVariable))
        {
            return false;
        }

        var expected = Environment.GetEnvironmentVariable(options.TokenEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var provided = header[prefix.Length..].Trim();
        if (provided.Length == 0)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(expected));
    }

    private static List<Uri> NormalizeOrigins(IEnumerable<string>? origins)
    {
        if (origins is null)
        {
            return [];
        }

        var normalized = new List<Uri>();
        foreach (var origin in origins)
        {
            var uri = NormalizeOrigin(origin);
            if (uri is not null)
            {
                normalized.Add(uri);
            }
        }

        return normalized;
    }

    private static Uri? NormalizeOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return uri;
    }
}
