using System.Text.Json;
using System.Text.Json.Serialization;
using SystemEnvironment = System.Environment;
using GrowthInspector.Core.Models;

namespace GrowthInspector.Seeder;

// ---------------------------------------------------------------------------
// seed 文件的原始形状（与 seed/growth/*.json 一一对应）。
// 所有类型都禁止未知字段：seed 里写错键名会直接报错，而不是被静默忽略。
// ---------------------------------------------------------------------------

/// <summary>seed/growth/project.json：项目、环境、MCP server 三件套。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ProjectSeedFile
{
    public ProjectSeed? Project { get; set; }

    public List<EnvironmentSeed> Environments { get; set; } = [];

    public List<McpServerSeed> McpServers { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ProjectSeed
{
    public string? Name { get; set; }

    public string? Code { get; set; }

    public string? LarkWebhookUrl { get; set; }

    public string? OpsContext { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class EnvironmentSeed
{
    public string? Name { get; set; }

    public bool IsDefault { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class McpServerSeed
{
    public string? Name { get; set; }

    /// <summary>可选的环境名；为空表示项目内所有环境共用。</summary>
    public string? Environment { get; set; }

    /// <summary>Stdio / Http，默认 Stdio。</summary>
    public string? Transport { get; set; }

    public string? Command { get; set; }

    /// <summary>stdio 传输的命令行参数，导入时序列化成 JSON 数组字符串。</summary>
    public List<string>? Arguments { get; set; }

    public string? Url { get; set; }

    public bool IsEnabled { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 60;

    public int MaxResultBytes { get; set; } = 8192;
}

/// <summary>seed/growth/runbooks.json：手册清单；正文给 body 或 bodyFile。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class RunbooksSeedFile
{
    public List<RunbookSeed> Runbooks { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class RunbookSeed
{
    public string? Title { get; set; }

    /// <summary>General / Specialized。</summary>
    public string? Kind { get; set; }

    /// <summary>Markdown 正文（与 bodyFile 二选一）。</summary>
    public string? Body { get; set; }

    /// <summary>相对 seed 目录的 Markdown 文件路径（与 body 二选一）。</summary>
    public string? BodyFile { get; set; }
}

/// <summary>seed/growth/checklists.json：检查单与检查项。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ChecklistsSeedFile
{
    public List<ChecklistSeed> Checklists { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ChecklistSeed
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Manual / Hourly / Daily。</summary>
    public string? Frequency { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>引用的手册标题（必须能在 runbooks.json 中找到）。</summary>
    public List<string> RunbookRefs { get; set; } = [];

    /// <summary>检查项；数组顺序即 SortOrder（从 1 开始）。</summary>
    public List<ChecklistItemSeed> Items { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ChecklistItemSeed
{
    public string? Title { get; set; }

    /// <summary>检查项正文：查什么 / 什么算健康 / 异常先看哪。</summary>
    public string? Body { get; set; }

    /// <summary>Info / Warning / Critical。</summary>
    public string? Severity { get; set; }
}

// ---------------------------------------------------------------------------
// 校验通过后的领域形状：枚举已解析、正文已读出、SortOrder 已定。
// ---------------------------------------------------------------------------

internal sealed record NormalizedSeed(
    ProjectSpec Project,
    IReadOnlyList<EnvironmentSpec> Environments,
    IReadOnlyList<McpServerSpec> McpServers,
    IReadOnlyList<RunbookSpec> Runbooks,
    IReadOnlyList<ChecklistSpec> Checklists);

internal sealed record ProjectSpec(string Name, string Code, string? LarkWebhookUrl, string? OpsContext);

internal sealed record EnvironmentSpec(string Name, bool IsDefault);

internal sealed record McpServerSpec(
    string Name,
    string? EnvironmentName,
    McpTransport Transport,
    string? Command,
    string? ArgumentsJson,
    string? Url,
    bool IsEnabled,
    int TimeoutSeconds,
    int MaxResultBytes);

internal sealed record RunbookSpec(string Title, RunbookKind Kind, string Body);

internal sealed record ChecklistSpec(
    string Name,
    string? Description,
    ChecklistFrequency Frequency,
    bool IsEnabled,
    IReadOnlyList<string> RunbookTitles,
    IReadOnlyList<ChecklistItemSpec> Items);

internal sealed record ChecklistItemSpec(
    int SortOrder,
    string Title,
    string Body,
    Severity? Severity);

/// <summary>seed 校验失败：一次性列出全部错误，便于一次改完。</summary>
internal sealed class SeedValidationException(IReadOnlyList<string> errors)
    : Exception($"seed 校验未通过，共 {errors.Count} 处问题：" + SystemEnvironment.NewLine + string.Join(SystemEnvironment.NewLine, errors.Select(e => "  - " + e)))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
