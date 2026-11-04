namespace GrowthInspector.Core.Models;

/// <summary>
/// 登记的 MCP server。MCP 是框架唯一的数据源接入方式：新增数据源 = 登记一个 server。
/// </summary>
public sealed class McpServer
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    /// <summary>可选的环境绑定；为空表示项目内所有环境共用。</summary>
    public Guid? EnvironmentId { get; set; }

    public Environment? Environment { get; set; }

    public string Name { get; set; } = string.Empty;

    public McpTransport Transport { get; set; } = McpTransport.Stdio;

    /// <summary>stdio 传输的启动命令。</summary>
    public string? Command { get; set; }

    /// <summary>stdio 传输的命令行参数（JSON 数组）。</summary>
    public string? ArgumentsJson { get; set; }

    /// <summary>HTTP 传输的端点地址。</summary>
    public string? Url { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>归档标记。</summary>
    public bool IsArchived { get; set; }

    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>工具结果入库的单条截断上限（字节），默认 8 KB。</summary>
    public int MaxResultBytes { get; set; } = 8192;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
