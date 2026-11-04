using System.Net;
using System.Text.Json;

namespace GrowthInspector.Tests.Management;

/// <summary>管理 MCP：鉴权、Origin/Host 白名单、工具分级与一条工具调用端到端。</summary>
public sealed class ManagementMcpTests
{
    private const string InitializeRequest = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"tests","version":"1.0"}}}
        """;

    [Fact]
    public async Task Request_WithoutToken_Returns401()
    {
        await using var host = await ManagementMcpTestHost.StartAsync();
        using var response = await host.PostRawAsync(InitializeRequest, token: null, origin: host.AllowedOrigin);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Request_WithWrongToken_Returns401()
    {
        await using var host = await ManagementMcpTestHost.StartAsync();
        using var response = await host.PostRawAsync(InitializeRequest, token: "wrong", origin: host.AllowedOrigin);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Request_FromDisallowedOrigin_Returns403()
    {
        await using var host = await ManagementMcpTestHost.StartAsync();
        using var response = await host.PostRawAsync(
            InitializeRequest,
            token: ManagementMcpTestHost.Token,
            origin: "http://evil.example");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Request_WithDisallowedHost_Returns403()
    {
        await using var host = await ManagementMcpTestHost.StartAsync();
        using var response = await host.PostRawAsync(
            InitializeRequest,
            token: ManagementMcpTestHost.Token,
            origin: host.AllowedOrigin,
            host: "evil.example:9999");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ToolsList_OnlyReadTools_ByDefault()
    {
        await using var host = await ManagementMcpTestHost.StartAsync();
        var tools = await host.ListToolsAsync();

        var names = ReadToolNames(tools);
        Assert.Contains("project_list", names);
        Assert.Contains("checklist_get", names);
        Assert.Contains("check_result_list", names);
        Assert.Contains("finding_list", names);
        Assert.Contains("run_summary_get", names);
        Assert.Contains("tool_call_list", names);

        Assert.DoesNotContain("project_create", names);
        Assert.DoesNotContain("checklist_update", names);
        Assert.DoesNotContain("checklist_trigger", names);

        var projectList = FindTool(tools, "project_list");
        var annotations = projectList.GetProperty("annotations");
        Assert.True(annotations.GetProperty("readOnlyHint").GetBoolean());
        Assert.False(annotations.GetProperty("destructiveHint").GetBoolean());
        Assert.True(annotations.GetProperty("idempotentHint").GetBoolean());
        Assert.False(annotations.GetProperty("openWorldHint").GetBoolean());

        // 只读工具也强制分页参数。
        var properties = projectList.GetProperty("inputSchema").GetProperty("properties");
        Assert.True(properties.TryGetProperty("limit", out _));
        Assert.True(properties.TryGetProperty("cursor", out _));
    }

    [Fact]
    public async Task ToolsList_AddsConfigWriteTools_WhenEnabledOnly()
    {
        await using var host = await ManagementMcpTestHost.StartAsync(configWrite: true, adminWrite: false);
        var names = ReadToolNames(await host.ListToolsAsync());

        Assert.Contains("project_create", names);
        Assert.Contains("checklist_item_update", names);
        Assert.Contains("checklist_runbook_add", names);
        Assert.DoesNotContain("checklist_trigger", names);
    }

    [Fact]
    public async Task ToolsList_AddsAdminWriteTools_WhenEnabledOnly()
    {
        await using var host = await ManagementMcpTestHost.StartAsync(configWrite: false, adminWrite: true);
        var tools = await host.ListToolsAsync();
        var names = ReadToolNames(tools);

        var trigger = FindTool(tools, "checklist_trigger");
        var annotations = trigger.GetProperty("annotations");
        Assert.False(annotations.GetProperty("readOnlyHint").GetBoolean());
        Assert.True(annotations.GetProperty("openWorldHint").GetBoolean());
        Assert.DoesNotContain("project_create", names);
    }

    [Fact]
    public async Task EndToEnd_CreateProjectThenListIt_WithMaskedWebhook()
    {
        await using var host = await ManagementMcpTestHost.StartAsync(configWrite: true);

        var createResult = await host.CallToolAsync("project_create", new
        {
            name = "E2E 项目",
            code = "e2e",
            larkWebhookUrl = "https://open.larksuite.com/hook/E2E1234",
        });
        Assert.False(
            createResult.TryGetProperty("isError", out var isError) && isError.GetBoolean());

        using var created = JsonDocument.Parse(ManagementMcpTestHost.ReadToolText(createResult));
        Assert.True(created.RootElement.GetProperty("ok").GetBoolean());
        var projectId = created.RootElement.GetProperty("data").GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(projectId));

        var getResult = await host.CallToolAsync("project_get", new { id = projectId });
        using var fetched = JsonDocument.Parse(ManagementMcpTestHost.ReadToolText(getResult));
        var project = fetched.RootElement.GetProperty("data");
        Assert.Equal("E2E 项目", project.GetProperty("name").GetString());
        Assert.Equal("****1234", project.GetProperty("larkWebhookUrl").GetString());

        var listResult = await host.CallToolAsync("project_list", new { limit = 10 });
        using var listed = JsonDocument.Parse(ManagementMcpTestHost.ReadToolText(listResult));
        var page = listed.RootElement.GetProperty("data");
        Assert.True(page.GetProperty("items").GetArrayLength() >= 1);
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);

        // 名称为必填，服务端校验错误以 ok=false 返回而不是协议错误。
        var invalidResult = await host.CallToolAsync("project_create", new { name = "  " });
        using var invalid = JsonDocument.Parse(ManagementMcpTestHost.ReadToolText(invalidResult));
        Assert.False(invalid.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("不能为空", invalid.RootElement.GetProperty("error").GetString());
    }

    private static HashSet<string> ReadToolNames(JsonElement tools)
        => tools.EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

    private static JsonElement FindTool(JsonElement tools, string name)
        => tools.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == name);
}
