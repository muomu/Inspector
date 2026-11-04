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

/// <summary>创建 agent 工具集的输入：Project / Environment / 检查项与角色。</summary>
public sealed record AgentToolsetRequest(
    Guid ProjectId,
    Guid EnvironmentId,
    Guid ChecklistRunId,
    Guid? ChecklistItemId,
    AgentRole AgentRole);

/// <summary>
/// 按角色组装工具集：
/// 执行 = MCP 工具 + 手册检索 / 披露 / 列表 + 手册写；
/// 反思 = 手册检索 / 披露 / 列表 + 手册写；
/// 摘要 = 无工具。
/// 三类角色都不暴露管理 CRUD。
/// </summary>
public sealed class AgentToolsetFactory(
    IDbContextFactory<GrowthInspectorDbContext> dbContextFactory,
    IMcpToolInvoker mcpToolInvoker,
    IRunbookWriteService? runbookWriteService,
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
        if (request.AgentRole is AgentRole.Execution or AgentRole.Reflection)
        {
            RegisterRunbookTools(registry, request.ProjectId);
        }

        var context = new AgentToolCallContext(
            request.ProjectId,
            request.EnvironmentId,
            request.ChecklistRunId,
            request.ChecklistItemId,
            request.AgentRole);

        var toolset = new AgentToolset(
            registry,
            toolCallAuditor,
            context,
            mcpToolInvoker,
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

            foreach (var descriptor in descriptors)
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
            options,
            runbookWriteService,
            loggerFactory.CreateLogger<RunbookToolService>());

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

        registry.AddLocal(AIFunctionFactory.Create(
            (string runbookId, string? section, string content, CancellationToken cancellationToken) =>
                tools.UpdateSectionAsync(projectId, runbookId, section, content, cancellationToken),
            name: AgentToolNames.UpdateRunbookSection,
            description: "改写（或追加）手册的指定章节；失败返回 {ok:false,error}，不抛异常。",
            serializerOptions: JsonOptions));
    }
}
