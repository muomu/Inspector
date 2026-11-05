namespace GrowthInspector.Core.Options;

/// <summary>巡检框架的运行期配置，对应 appsettings.json 的 Inspector 节。</summary>
public sealed class InspectionOptions
{
    public const string SectionName = "Inspector";

    /// <summary>执行侧审计留存天数（执行历史 / 结果 / 工具调用 / 摘要）。</summary>
    public int AuditRetentionDays { get; set; } = 90;

    /// <summary>工具结果单条入库截断上限（字节）。</summary>
    public int ToolResultMaxBytes { get; set; } = 8192;

    /// <summary>检查单之间并行执行的上限（检查项在检查单内串行，不再有项级并发）。</summary>
    public int MaxConcurrentChecklists { get; set; } = 4;

    /// <summary>调度器轮询间隔（秒）。</summary>
    public int SchedulerTickSeconds { get; set; } = 30;

    /// <summary>每小时频率的检查单在每小时第几分钟触发。</summary>
    public int HourlyScheduleMinute { get; set; } = 0;

    /// <summary>每日频率的检查单触发时刻（HH:mm，服务器本地时区）。</summary>
    public string DailyScheduleTime { get; set; } = "10:00";

    /// <summary>反思 agent 单检查项的最大工具调用轮次（执行 agent 不限轮次）。</summary>
    public int AgentMaxTurns { get; set; } = 12;

    /// <summary>反思 agent 单个检查项的执行超时（秒）。执行 agent 不设超时。</summary>
    public int AgentItemTimeoutSeconds { get; set; } = 180;

    /// <summary>手册检索工具单次返回条数上限。</summary>
    public int RunbookSearchLimit { get; set; } = 5;

    /// <summary>日报生成与发送的时区（IANA，如 Asia/Shanghai）。</summary>
    public string TimeZone { get; set; } = "Asia/Shanghai";
}

/// <summary>DeepSeek（OpenAI 兼容）接入配置，对应 appsettings.json 的 DeepSeek 节。密钥只走环境变量。</summary>
public sealed class DeepSeekOptions
{
    public const string SectionName = "DeepSeek";

    /// <summary>API base URL。</summary>
    public string BaseUrl { get; set; } = "https://api.deepseek.com";

    /// <summary>执行 agent 使用的模型。</summary>
    public string Model { get; set; } = "deepseek-flash";

    /// <summary>摘要 agent 使用的模型（可与执行模型不同）。</summary>
    public string SummaryModel { get; set; } = "deepseek-flash";

    /// <summary>执行 agent 是否开启 thinking。v1 关闭：thinking + tools 需全程回传 reasoning_content。</summary>
    public bool ThinkingForExecution { get; set; }

    /// <summary>摘要 agent 是否开启 thinking。</summary>
    public bool ThinkingForSummary { get; set; } = true;

    /// <summary>单次请求超时（秒）。</summary>
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>管理 MCP server 的配置，对应 appsettings.json 的 ManagementMcp 节。</summary>
public sealed class ManagementMcpOptions
{
    public const string SectionName = "ManagementMcp";

    /// <summary>是否托管管理 MCP 端点。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>MCP 端点路径（挂在本进程的 Blazor 应用上）。</summary>
    public string Path { get; set; } = "/mcp";

    /// <summary>只允许的绑定地址（仅 loopback）。</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>存放 bearer token 的环境变量名。</summary>
    public string TokenEnvironmentVariable { get; set; } = "INSPECTOR_MCP_TOKEN";

    /// <summary>开启 config-write 的环境变量名（值为 true / 1 时开启）。</summary>
    public string ConfigWriteEnvironmentVariable { get; set; } = "INSPECTOR_MCP_CONFIG_WRITE";

    /// <summary>开启 admin-write 的环境变量名（值为 true / 1 时开启）。</summary>
    public string AdminWriteEnvironmentVariable { get; set; } = "INSPECTOR_MCP_ADMIN_WRITE";

    /// <summary>允许的 Origin 列表（含端口）。</summary>
    public string[] AllowedOrigins { get; set; } =
    [
        "http://localhost:5273",
        "http://127.0.0.1:5273",
    ];
}
