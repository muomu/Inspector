using GrowthInspector.Agents;
using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Options;
using GrowthInspector.Data;
using GrowthInspector.Notifications;
using GrowthInspector.Orchestration;
using GrowthInspector.Web.Components;
using GrowthInspector.Web.Scheduling;
using GrowthInspector.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// 配置节：巡检运行参数与 DeepSeek 接入参数（密钥只走环境变量 DEEPSEEK_API_KEY）。
builder.Services.Configure<InspectionOptions>(
    builder.Configuration.GetSection(InspectionOptions.SectionName));
builder.Services.Configure<DeepSeekOptions>(
    builder.Configuration.GetSection(DeepSeekOptions.SectionName));

var sqlitePath = builder.Configuration["Inspector:SqlitePath"];
if (string.IsNullOrWhiteSpace(sqlitePath))
{
    sqlitePath = Path.Combine(builder.Environment.ContentRootPath, "data", "inspector.db");
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddGrowthInspectorData(sqlitePath);

var deepSeekOptions = builder.Configuration
    .GetSection(DeepSeekOptions.SectionName)
    .Get<DeepSeekOptions>() ?? new DeepSeekOptions();

builder.Services.AddGrowthInspectorAgents(deepSeekOptions);
builder.Services.AddGrowthInspectorOrchestration();
builder.Services.AddGrowthInspectorNotifications();

builder.Services.AddScoped<IDailyReportGenerator, DailyReportGenerator>();
builder.Services.AddScoped<DailyReportService>();
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

// 本地工具：启动时自动建库（EnsureCreated），不做迁移。
await app.Services.EnsureGrowthInspectorDatabaseAsync();

app.Run();
