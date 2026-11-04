using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GrowthInspector.Agents;
using GrowthInspector.Core.Options;
using GrowthInspector.Data;
using GrowthInspector.Management;
using GrowthInspector.Orchestration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GrowthInspector.Tests.Management;

/// <summary>
/// 管理 MCP 端到端测试宿主：真实 Kestrel + 真实 Streamable HTTP 端点，
/// token / 写开关用本次测试专属的环境变量名，避免并行测试互相干扰。
/// </summary>
internal sealed class ManagementMcpTestHost : IAsyncDisposable
{
    public const string Token = "test";

    private readonly WebApplication application;
    private readonly string databasePath;
    private readonly string tokenVariable;
    private readonly string configWriteVariable;
    private readonly string adminWriteVariable;

    private ManagementMcpTestHost(
        WebApplication application,
        string databasePath,
        string baseAddress,
        string tokenVariable,
        string configWriteVariable,
        string adminWriteVariable)
    {
        this.application = application;
        this.databasePath = databasePath;
        BaseAddress = baseAddress;
        this.tokenVariable = tokenVariable;
        this.configWriteVariable = configWriteVariable;
        this.adminWriteVariable = adminWriteVariable;
        Client = new HttpClient { BaseAddress = new Uri(baseAddress) };
    }

    public string BaseAddress { get; }

    public HttpClient Client { get; }

    public string AllowedOrigin => BaseAddress;

    public static async Task<ManagementMcpTestHost> StartAsync(bool configWrite = false, bool adminWrite = false)
    {
        var port = FindFreePort();
        var baseAddress = $"http://127.0.0.1:{port}";
        var tokenVariable = $"INSPECTOR_MCP_TEST_TOKEN_{Guid.NewGuid():N}";
        var configWriteVariable = $"INSPECTOR_MCP_TEST_CONFIG_WRITE_{Guid.NewGuid():N}";
        var adminWriteVariable = $"INSPECTOR_MCP_TEST_ADMIN_WRITE_{Guid.NewGuid():N}";

        Environment.SetEnvironmentVariable(tokenVariable, Token);
        Environment.SetEnvironmentVariable(configWriteVariable, configWrite ? "true" : null);
        Environment.SetEnvironmentVariable(adminWriteVariable, adminWrite ? "1" : null);

        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"growth-inspector-mcp-test-{Guid.NewGuid():N}.db");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
        });

        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls(baseAddress);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ManagementMcp:Enabled"] = "true",
            ["ManagementMcp:Path"] = "/mcp",
            ["ManagementMcp:TokenEnvironmentVariable"] = tokenVariable,
            ["ManagementMcp:ConfigWriteEnvironmentVariable"] = configWriteVariable,
            ["ManagementMcp:AdminWriteEnvironmentVariable"] = adminWriteVariable,
            ["ManagementMcp:AllowedOrigins:0"] = baseAddress,
            ["ManagementMcp:AllowedOrigins:1"] = $"http://localhost:{port}",
        });

        builder.Services.Configure<InspectionOptions>(_ => { });
        builder.Services.AddGrowthInspectorData(databasePath);
        builder.Services.AddGrowthInspectorOrchestration();
        builder.Services.AddGrowthInspectorAgents(new DeepSeekOptions());
        builder.Services.AddGrowthInspectorManagement(builder.Configuration);

        var application = builder.Build();
        application.MapGrowthInspectorManagementMcp();

        await application.StartAsync();
        await application.Services.EnsureGrowthInspectorDatabaseAsync();

        return new ManagementMcpTestHost(
            application,
            databasePath,
            baseAddress,
            tokenVariable,
            configWriteVariable,
            adminWriteVariable);
    }

    public async Task<HttpResponseMessage> PostRawAsync(
        string json,
        string? token = Token,
        string? origin = null,
        string? host = null,
        string path = "/mcp")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        if (host is not null)
        {
            request.Headers.Host = host;
        }

        return await Client.SendAsync(request);
    }

    public async Task<JsonElement> InitializeAsync()
    {
        using var response = await PostRawAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "growth-inspector-tests", version = "1.0" },
            },
        }), origin: AllowedOrigin);

        response.EnsureSuccessStatusCode();
        var message = await ReadJsonRpcMessageAsync(response);
        Assert.False(message.TryGetProperty("error", out _));
        return message;
    }

    public async Task<JsonElement> ListToolsAsync()
    {
        await InitializeAsync();
        using var response = await PostRawAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/list",
            @params = new { },
        }), origin: AllowedOrigin);

        response.EnsureSuccessStatusCode();
        var message = await ReadJsonRpcMessageAsync(response);
        Assert.False(message.TryGetProperty("error", out var error), error.ToString());
        return message.GetProperty("result").GetProperty("tools");
    }

    public async Task<JsonElement> CallToolAsync(string name, object arguments)
    {
        await InitializeAsync();
        using var response = await PostRawAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "tools/call",
            @params = new { name, arguments },
        }), origin: AllowedOrigin);

        response.EnsureSuccessStatusCode();
        var message = await ReadJsonRpcMessageAsync(response);
        Assert.False(message.TryGetProperty("error", out var error), error.ToString());
        return message.GetProperty("result");
    }

    public static string ReadToolText(JsonElement callResult)
    {
        var content = callResult.GetProperty("content");
        Assert.True(content.GetArrayLength() > 0);
        return content[0].GetProperty("text").GetString() ?? string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await application.StopAsync();
        await application.DisposeAsync();

        Environment.SetEnvironmentVariable(tokenVariable, null);
        Environment.SetEnvironmentVariable(configWriteVariable, null);
        Environment.SetEnvironmentVariable(adminWriteVariable, null);

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = databasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task<JsonElement> ReadJsonRpcMessageAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var contentType = response.Content.Headers.ContentType?.MediaType;

        if (string.Equals(contentType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var dataLine = body
                .Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .FirstOrDefault(line => line.StartsWith("data:", StringComparison.Ordinal));
            Assert.NotNull(dataLine);
            body = dataLine!["data:".Length..].Trim();
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
