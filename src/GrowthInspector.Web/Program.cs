using GrowthInspector.Agents;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Options;
using GrowthInspector.Data;
using GrowthInspector.Management;
using GrowthInspector.Notifications;
using GrowthInspector.Orchestration;
using GrowthInspector.Web.Components;
using GrowthInspector.Web.Scheduling;
using GrowthInspector.Web.Services;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// 配置节：巡检运行参数与 DeepSeek 接入参数。
// API key 不再读环境变量：由设置页写入 SQLite（DataProtection 加密），下面的 DeepSeek 节只提供出厂预填值与 thinking / 超时。
builder.Services.Configure<InspectionOptions>(
    builder.Configuration.GetSection(InspectionOptions.SectionName));
builder.Services.Configure<DeepSeekOptions>(
    builder.Configuration.GetSection(DeepSeekOptions.SectionName));

// 密钥保护：密钥环固定在本机 data/protection-keys，与数据库同生命周期；
// 密钥环丢失会导致已存的 key 无法解密（读出来按「未配置」处理，重新填写即可）。
var protectionKeysPath = Path.Combine(builder.Environment.ContentRootPath, "data", "protection-keys");
Directory.CreateDirectory(protectionKeysPath);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(protectionKeysPath))
    .SetApplicationName("GrowthInspector");

var sqlitePath = builder.Configuration["Inspector:SqlitePath"];
if (string.IsNullOrWhiteSpace(sqlitePath))
{
    sqlitePath = Path.Combine(builder.Environment.ContentRootPath, "data", "inspector.db");
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddGrowthInspectorData(sqlitePath);

// 执行侧审计留存清理：按 Inspector:AuditRetentionDays 定时清理过期执行数据（TPG-2705）。
builder.Services.AddGrowthInspectorAuditPruning();

var deepSeekOptions = builder.Configuration
    .GetSection(DeepSeekOptions.SectionName)
    .Get<DeepSeekOptions>() ?? new DeepSeekOptions();

builder.Services.AddGrowthInspectorAgents(deepSeekOptions);
builder.Services.AddGrowthInspectorOrchestration();
builder.Services.AddGrowthInspectorNotifications();
builder.Services.AddGrowthInspectorManagement(builder.Configuration);

builder.Services.AddScoped<IDailyReportGenerator, DailyReportGenerator>();
builder.Services.AddScoped<DailyReportService>();
// 顶栏项目 / 环境上下文：每次请求按 query 解析，默认落到项目默认环境。
builder.Services.AddScoped<WorkspaceContextService>();
builder.Services.AddHostedService<InspectionSchedulerHostedService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// 管理 MCP server：Streamable HTTP，挂在主进程上（仅 loopback；工具面由 TPG-2712 提供）。
app.MapGrowthInspectorManagementMcp();

// 本地工具：启动时自动建库（EnsureCreated），不做迁移。
await app.Services.EnsureGrowthInspectorDatabaseAsync();

app.Run();
