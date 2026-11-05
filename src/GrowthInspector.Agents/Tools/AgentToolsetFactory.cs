using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using GrowthInspector.Core.Options;
using GrowthInspector.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrowthInspector.Agents.Tools;

/// <summary>创建 agent 工具集的输入：Project / Environment / 执行轮次与角色。</summary>
public sealed record AgentToolsetRequest(
    Guid ProjectId,
    Guid EnvironmentId,
    Guid ChecklistRunId,
    Guid? ChecklistItemId,
    AgentRole AgentRole);

/// <summary>
/// 按角色组装工具集：执行 = MCP 工具 + 手册检索 / 披露 / 列表（只读）；摘要 = 无工具。
/// 工具手册只读，改写归人工。MCP 工具按 server 名与工具名排序注册，
/// 保证工具声明顺序确定，provider 侧前缀缓存不被顺序抖动打散。
/// </summary>
public sealed class AgentToolsetFactory(
    IDbContextFactory<GrowthInspectorDbContext> dbContextFactory,
    IMcpToolInvoker mcpToolInvoker,
    RunbookSearchService runbookSearchService,
    ToolCallAuditor toolCallAuditor,
    IOptions<InspectionOptions> options,
    ILoggerFactory loggerFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private readonly ILogger<AgentToolsetFactory> logger = loggerFactory.CreateLogger<AgentToolsetFactory>();

    public async Task<AgentToolset> CreateAsync(
        AgentToolsetRequest request,
        CancellationToken cancellationToken = default)
    {
        var registry = new AgentToolRegistry();
        if (request.AgentRole == AgentRole.Execution)
        {
            RegisterRunbookTools(registry, request.ProjectId);
        }

        var toolset = new AgentToolset(
            registry,
            toolCallAuditor,
            new AgentToolCallContext(
                request.ProjectId,
                request.EnvironmentId,
                request.ChecklistRunId,
                request.ChecklistItemId,
                request.AgentRole),
            mcpToolInvoker,
            options,
            loggerFactory.CreateLogger<AgentToolset>());

        if (request.AgentRole == AgentRole.Execution)
        {
            await RefreshMcpToolsAsync(toolset, request.ProjectId, request.EnvironmentId, cancellationToken);
        }

        return toolset;
    }

    /// <summary>按当前 Project / Environment 下启用且未归档的 McpServer 重新拉取并注册 MCP 工具。</summary>
    public async Task RefreshMcpToolsAsync(
        AgentToolset toolset,
        Guid projectId,
        Guid environmentId,
        CancellationToken cancellationToken = default)
    {
        toolset.Registry.RemoveMcpTools();

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var servers = await db.McpServers
            .AsNoTracking()
            .Where(server => server.ProjectId == projectId
                && server.IsEnabled
                && !server.IsArchived
                && (server.EnvironmentId == null || server.EnvironmentId == environmentId))
            .OrderBy(server => server.Name)
            .Select(server => new { server.Id, server.Name })
            .ToListAsync(cancellationToken);

        foreach (var server in servers)
        {
            IReadOnlyList<McpToolDescriptor> descriptors;
            try
            {
                descriptors = await mcpToolInvoker.ListToolsAsync(server.Id, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                toolset.Registry.RecordRegistrationError($"MCP server「{server.Name}」拉取工具失败：{ex.Message}");
                logger.LogWarning(ex, "MCP server {ServerName} 拉取工具失败，跳过。", server.Name);
                continue;
            }

            // 注册顺序确定（server 名 → 工具名），工具声明作为请求前缀的一部分才能稳定命中缓存。
            foreach (var descriptor in descriptors.OrderBy(descriptor => descriptor.Name, StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(descriptor.Name))
                {
                    continue;
                }

                var added = toolset.Registry.TryAddMcpTool(
                    server.Name,
                    descriptor.Name,
                    descriptor.Description,
                    descriptor.InputSchemaJson,
                    server.Id,
                    async (argumentsJson, token) =>
                    {
                        var result = await mcpToolInvoker.CallToolAsync(
                            server.Id,
                            descriptor.Name,
                            argumentsJson,
                            context: null,
                            token);
                        return result.Succeeded
                            ? result.ResultText ?? string.Empty
                            : result.ErrorMessage ?? "MCP 工具调用失败。";
                    },
                    out var error);

                if (!added)
                {
                    logger.LogWarning("MCP 工具 {ToolName} 未注册：{Error}", descriptor.Name, error);
                }
            }
        }
    }

    private void RegisterRunbookTools(AgentToolRegistry registry, Guid projectId)
    {
        var tools = new RunbookToolService(
            dbContextFactory,
            runbookSearchService,
            options);

        registry.AddLocal(AIFunctionFactory.Create(
            (string query, CancellationToken cancellationToken) => tools.SearchAsync(projectId, query, cancellationToken),
            name: AgentToolNames.SearchRunbooks,
            description: "检索当前 Project 的专用运维手册（未归档），按中文子串命中，返回手册 Id、标题与片段。",
            serializerOptions: JsonOptions));

        registry.AddLocal(AIFunctionFactory.Create(
            (string runbookId, string? section, CancellationToken cancellationToken) =>
                tools.ReadAsync(projectId, runbookId, section, cancellationToken),
            name: AgentToolNames.ReadRunbook,
            description: "读取手册全文；传 section 时只返回该章节，用于渐进披露。",
            serializerOptions: JsonOptions));

        registry.AddLocal(AIFunctionFactory.Create(
            (CancellationToken cancellationToken) => tools.ListAsync(projectId, cancellationToken),
            name: AgentToolNames.ListRunbooks,
            description: "列出当前 Project 未归档手册的 Id、ProjectId、Kind 与标题。",
            serializerOptions: JsonOptions));
    }
}
