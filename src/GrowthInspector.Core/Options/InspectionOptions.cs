namespace GrowthInspector.Core.Options;

/// <summary>巡检框架的运行期配置，对应 appsettings.json 的 Inspector 节。</summary>
public sealed class InspectionOptions
{
    public const string SectionName = "Inspector";

    /// <summary>审计留存天数。</summary>
    public int AuditRetentionDays { get; set; } = 90;

    /// <summary>工具结果单条入库截断上限（字节）。</summary>
    public int ToolResultMaxBytes { get; set; } = 8192;

    /// <summary>检查项 fan-out 并发上限。</summary>
    public int ChecklistItemConcurrency { get; set; } = 8;

    /// <summary>调度器轮询间隔（秒）。</summary>
    public int SchedulerTickSeconds { get; set; } = 30;
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
}
