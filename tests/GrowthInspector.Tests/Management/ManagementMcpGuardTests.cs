using System.Net;
using GrowthInspector.Core.Options;
using GrowthInspector.Management;
using Microsoft.AspNetCore.Http;

namespace GrowthInspector.Tests.Management;

/// <summary>管理 MCP 请求守卫的纯单测：loopback 限制、Origin/Host 白名单、token 校验。</summary>
public sealed class ManagementMcpGuardTests
{
    [Fact]
    public void LoopbackBind_AllowsLoopbackClient()
    {
        var context = CreateContext(IPAddress.Loopback);
        Assert.True(ManagementMcpRequestGuard.IsAllowedClientAddress(context, LoopbackOptions()));
    }

    [Fact]
    public void LoopbackBind_RejectsNonLoopbackClient()
    {
        var context = CreateContext(IPAddress.Parse("203.0.113.9"));
        Assert.False(ManagementMcpRequestGuard.IsAllowedClientAddress(context, LoopbackOptions()));
    }

    [Fact]
    public void LoopbackBind_AllowsIpv4MappedLoopbackClient()
    {
        var context = CreateContext(IPAddress.Parse("::ffff:127.0.0.1"));
        Assert.True(ManagementMcpRequestGuard.IsAllowedClientAddress(context, LoopbackOptions()));
    }

    [Fact]
    public void NonLoopbackBind_DoesNotRestrictClientAddress()
    {
        var context = CreateContext(IPAddress.Parse("203.0.113.9"));
        var options = LoopbackOptions();
        options.BindAddress = "0.0.0.0";
        Assert.True(ManagementMcpRequestGuard.IsAllowedClientAddress(context, options));
    }

    [Fact]
    public void HostAndOrigin_MustMatchAllowedOrigins()
    {
        var options = LoopbackOptions();
        options.AllowedOrigins = ["http://127.0.0.1:5273"];

        var allowed = CreateContext(IPAddress.Loopback);
        allowed.Request.Host = new HostString("127.0.0.1", 5273);
        allowed.Request.Headers.Origin = "http://127.0.0.1:5273";
        Assert.True(ManagementMcpRequestGuard.IsAllowedHostAndOrigin(allowed.Request, options));

        var badOrigin = CreateContext(IPAddress.Loopback);
        badOrigin.Request.Host = new HostString("127.0.0.1", 5273);
        badOrigin.Request.Headers.Origin = "http://evil.example";
        Assert.False(ManagementMcpRequestGuard.IsAllowedHostAndOrigin(badOrigin.Request, options));

        var badHost = CreateContext(IPAddress.Loopback);
        badHost.Request.Host = new HostString("evil.example", 5273);
        Assert.False(ManagementMcpRequestGuard.IsAllowedHostAndOrigin(badHost.Request, options));
    }

    [Fact]
    public void Token_MustMatchConfiguredEnvironmentVariable()
    {
        var variable = $"INSPECTOR_MCP_GUARD_TEST_{Guid.NewGuid():N}";
        var options = LoopbackOptions();
        options.TokenEnvironmentVariable = variable;
        var context = CreateContext(IPAddress.Loopback);

        // 环境变量缺失时 fail-closed。
        Assert.False(ManagementMcpRequestGuard.HasValidToken(context.Request, options));

        Environment.SetEnvironmentVariable(variable, "test-token");
        try
        {
            context.Request.Headers.Authorization = "Bearer test-token";
            Assert.True(ManagementMcpRequestGuard.HasValidToken(context.Request, options));

            context.Request.Headers.Authorization = "Bearer wrong";
            Assert.False(ManagementMcpRequestGuard.HasValidToken(context.Request, options));

            context.Request.Headers.Authorization = "Basic test-token";
            Assert.False(ManagementMcpRequestGuard.HasValidToken(context.Request, options));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    private static ManagementMcpOptions LoopbackOptions() => new()
    {
        BindAddress = "127.0.0.1",
        AllowedOrigins = ["http://127.0.0.1:5273"],
        TokenEnvironmentVariable = "INSPECTOR_MCP_TOKEN",
    };

    private static DefaultHttpContext CreateContext(IPAddress remoteAddress)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remoteAddress;
        context.Request.Host = new HostString("127.0.0.1", 5273);
        return context;
    }
}
