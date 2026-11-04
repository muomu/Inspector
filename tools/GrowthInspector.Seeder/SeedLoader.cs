using System.Text.Json;
using System.Text.Json.Serialization;
using GrowthInspector.Core.Models;

namespace GrowthInspector.Seeder;

/// <summary>
/// 读取并校验 seed 目录。校验不通过时不碰数据库：一次性抛 <see cref="SeedValidationException"/>。
/// </summary>
internal static class SeedLoader
{
    private const string ProjectFileName = "project.json";
    private const string RunbooksFileName = "runbooks.json";
    private const string ChecklistsFileName = "checklists.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public static async Task<NormalizedSeed> LoadAsync(string seedDirectory, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        if (!Directory.Exists(seedDirectory))
        {
            throw new SeedValidationException([$"seed 目录不存在：{seedDirectory}"]);
        }

        var projectFile = await ReadAsync<ProjectSeedFile>(seedDirectory, ProjectFileName, errors, cancellationToken);
        var runbooksFile = await ReadAsync<RunbooksSeedFile>(seedDirectory, RunbooksFileName, errors, cancellationToken);
        var checklistsFile = await ReadAsync<ChecklistsSeedFile>(seedDirectory, ChecklistsFileName, errors, cancellationToken);

        // 任一文件读不出来时直接报错，避免用半份 seed 建出残缺数据。
        if (projectFile is null || runbooksFile is null || checklistsFile is null)
        {
            throw new SeedValidationException(errors);
        }

        var project = BindProject(projectFile, errors);
        var environments = BindEnvironments(projectFile, errors);
        var mcpServers = BindMcpServers(projectFile, environments, errors);
        var runbooks = await BindRunbooksAsync(seedDirectory, runbooksFile, errors, cancellationToken);
        var checklists = BindChecklists(checklistsFile, runbooks, errors);

        if (errors.Count > 0)
        {
            throw new SeedValidationException(errors);
        }

        return new NormalizedSeed(project!, environments, mcpServers, runbooks, checklists);
    }

    private static async Task<T?> ReadAsync<T>(string seedDirectory, string fileName, List<string> errors, CancellationToken cancellationToken)
    {
        var path = Path.Combine(seedDirectory, fileName);
        if (!File.Exists(path))
        {
            errors.Add($"{fileName} 不存在（期望路径：{path}）");
            return default;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var value = await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken);
            if (value is null)
            {
                errors.Add($"{fileName} 是空的 JSON（null）");
            }

            return value;
        }
        catch (JsonException ex)
        {
            errors.Add($"{fileName} 不是合法 JSON：{ex.Message}");
            return default;
        }
    }

    private static ProjectSpec? BindProject(ProjectSeedFile file, List<string> errors)
    {
        if (file.Project is null)
        {
            errors.Add($"{ProjectFileName} 缺少 project 段");
            return null;
        }

        var name = RequireText(file.Project.Name, $"{ProjectFileName} project.name", errors);
        var code = RequireText(file.Project.Code, $"{ProjectFileName} project.code", errors);
        if (name is null || code is null)
        {
            return null;
        }

        return new ProjectSpec(name, code, file.Project.LarkWebhookUrl, file.Project.OpsContext);
    }

    private static IReadOnlyList<EnvironmentSpec> BindEnvironments(ProjectSeedFile file, List<string> errors)
    {
        if (file.Environments.Count == 0)
        {
            errors.Add($"{ProjectFileName} 至少要有一个 environment");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var environment in file.Environments)
        {
            var name = RequireText(environment.Name, $"{ProjectFileName} environments[].name", errors);
            if (name is not null && !seen.Add(name))
            {
                errors.Add($"{ProjectFileName} 环境名重复：{name}");
            }
        }

        if (file.Environments.Count > 0 && file.Environments.All(x => !x.IsDefault))
        {
            errors.Add($"{ProjectFileName} 必须有一个 isDefault=true 的环境");
        }

        if (file.Environments.Count(x => x.IsDefault) > 1)
        {
            errors.Add($"{ProjectFileName} 只能有一个 isDefault=true 的环境");
        }

        return file.Environments
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => new EnvironmentSpec(x.Name!, x.IsDefault))
            .ToArray();
    }

    private static IReadOnlyList<McpServerSpec> BindMcpServers(
        ProjectSeedFile file,
        IReadOnlyList<EnvironmentSpec> environments,
        List<string> errors)
    {
        var environmentNames = environments.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<McpServerSpec>();

        foreach (var seed in file.McpServers)
        {
            var name = RequireText(seed.Name, $"{ProjectFileName} mcpServers[].name", errors);
            if (name is null)
            {
                continue;
            }

            if (!seen.Add(name))
            {
                errors.Add($"{ProjectFileName} MCP server 名重复：{name}");
            }

            if (seed.Environment is not null && !environmentNames.Contains(seed.Environment))
            {
                errors.Add($"{ProjectFileName} MCP server {name} 绑定了未知环境：{seed.Environment}");
            }

            var transport = ParseEnum(seed.Transport ?? nameof(McpTransport.Stdio), McpTransport.Stdio,
                $"{ProjectFileName} mcpServers[{name}].transport", errors);

            string? argumentsJson = null;
            if (seed.Arguments is { Count: > 0 })
            {
                argumentsJson = JsonSerializer.Serialize(seed.Arguments);
            }

            if (transport == McpTransport.Stdio && string.IsNullOrWhiteSpace(seed.Command))
            {
                errors.Add($"{ProjectFileName} MCP server {name} 是 stdio 传输，必须给 command");
            }

            if (transport == McpTransport.Http && string.IsNullOrWhiteSpace(seed.Url))
            {
                errors.Add($"{ProjectFileName} MCP server {name} 是 http 传输，必须给 url");
            }

            if (seed.TimeoutSeconds <= 0)
            {
                errors.Add($"{ProjectFileName} MCP server {name} 的 timeoutSeconds 必须为正数");
            }

            if (seed.MaxResultBytes <= 0)
            {
                errors.Add($"{ProjectFileName} MCP server {name} 的 maxResultBytes 必须为正数");
            }

            result.Add(new McpServerSpec(
                name,
                seed.Environment,
                transport,
                seed.Command,
                argumentsJson,
                seed.Url,
                seed.IsEnabled,
                seed.TimeoutSeconds,
                seed.MaxResultBytes));
        }

        return result;
    }

    private static async Task<IReadOnlyList<RunbookSpec>> BindRunbooksAsync(
        string seedDirectory,
        RunbooksSeedFile file,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(seedDirectory);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<RunbookSpec>();

        foreach (var seed in file.Runbooks)
        {
            var title = RequireText(seed.Title, $"{RunbooksFileName} runbooks[].title", errors);
            if (title is null)
            {
                continue;
            }

            if (!seen.Add(title))
            {
                errors.Add($"{RunbooksFileName} 手册标题重复：{title}");
            }

            var kind = ParseEnum(seed.Kind, RunbookKind.Specialized, $"{RunbooksFileName} runbooks[{title}].kind", errors);
            var body = await ResolveBodyAsync(seedDirectory, root, seed, title, errors, cancellationToken);
            if (body is null)
            {
                continue;
            }

            result.Add(new RunbookSpec(title, kind, body));
        }

        if (result.Count(x => x.Kind == RunbookKind.General) == 0)
        {
            errors.Add($"{RunbooksFileName} 至少要有一份 kind=General 的通用手册");
        }

        return result;
    }

    private static async Task<string?> ResolveBodyAsync(
        string seedDirectory,
        string seedRoot,
        RunbookSeed seed,
        string title,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var hasInline = !string.IsNullOrWhiteSpace(seed.Body);
        var hasFile = !string.IsNullOrWhiteSpace(seed.BodyFile);
        if (hasInline == hasFile)
        {
            errors.Add($"{RunbooksFileName} 手册 {title} 必须且只能给 body / bodyFile 其中一个");
            return null;
        }

        if (hasInline)
        {
            return NormalizeMarkdown(seed.Body!);
        }

        var relative = seed.BodyFile!.Replace('\\', '/');
        var fullPath = Path.GetFullPath(Path.Combine(seedDirectory, relative));
        if (!fullPath.StartsWith(seedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(fullPath, seedRoot, StringComparison.Ordinal))
        {
            errors.Add($"{RunbooksFileName} 手册 {title} 的 bodyFile 越出 seed 目录：{relative}");
            return null;
        }

        if (!File.Exists(fullPath))
        {
            errors.Add($"{RunbooksFileName} 手册 {title} 的 bodyFile 不存在：{relative}");
            return null;
        }

        var text = await File.ReadAllTextAsync(fullPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            errors.Add($"{RunbooksFileName} 手册 {title} 的 bodyFile 是空文件：{relative}");
            return null;
        }

        return NormalizeMarkdown(text);
    }

    private static IReadOnlyList<ChecklistSpec> BindChecklists(
        ChecklistsSeedFile file,
        IReadOnlyList<RunbookSpec> runbooks,
        List<string> errors)
    {
        var runbookTitles = runbooks.Select(x => x.Title).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ChecklistSpec>();

        foreach (var seed in file.Checklists)
        {
            var name = RequireText(seed.Name, $"{ChecklistsFileName} checklists[].name", errors);
            if (name is null)
            {
                continue;
            }

            if (!seen.Add(name))
            {
                errors.Add($"{ChecklistsFileName} 检查单名重复：{name}");
            }

            var frequency = ParseEnum(seed.Frequency ?? nameof(ChecklistFrequency.Manual), ChecklistFrequency.Manual,
                $"{ChecklistsFileName} checklists[{name}].frequency", errors);

            foreach (var reference in seed.RunbookRefs)
            {
                if (!runbookTitles.Contains(reference))
                {
                    errors.Add($"{ChecklistsFileName} 检查单 {name} 引用了不存在的手册：{reference}");
                }
            }

            if (seed.Items.Count == 0)
            {
                errors.Add($"{ChecklistsFileName} 检查单 {name} 至少要有一个检查项");
            }

            var itemTitles = new HashSet<string>(StringComparer.Ordinal);
            var items = new List<ChecklistItemSpec>();
            for (var i = 0; i < seed.Items.Count; i++)
            {
                var item = BindItem(seed.Items[i], name, i + 1, errors);
                if (item is null)
                {
                    continue;
                }

                if (!itemTitles.Add(item.Title))
                {
                    errors.Add($"{ChecklistsFileName} 检查单 {name} 的检查项标题重复：{item.Title}");
                }

                items.Add(item);
            }

            result.Add(new ChecklistSpec(
                name,
                seed.Description,
                frequency,
                seed.IsEnabled,
                seed.RunbookRefs,
                items));
        }

        return result;
    }

    private static ChecklistItemSpec? BindItem(ChecklistItemSeed seed, string checklistName, int sortOrder, List<string> errors)
    {
        var scope = $"{ChecklistsFileName} 检查单 {checklistName} 第 {sortOrder} 项";
        var title = RequireText(seed.Title, scope + ".title", errors);
        if (title is null)
        {
            return null;
        }

        var operatorValue = seed.Operator is null
            ? (ComparisonOperator?)null
            : ParseEnum(seed.Operator, ComparisonOperator.Eq, scope + ".operator", errors);

        var severity = seed.Severity is null
            ? (Severity?)null
            : ParseEnum(seed.Severity, Severity.Info, scope + ".severity", errors);

        var threshold = ReadThreshold(seed.Threshold, scope, errors);
        var argumentsTemplate = ReadArguments(seed.Arguments, scope, errors);

        var toolName = seed.ToolName?.Trim();
        var hasAnyAssertionField = toolName is not null
            || operatorValue is not null
            || threshold is not null
            || seed.ValuePath is not null;

        // 结构断言是三件套：工具名 + 比较符 + 阈值。只给半套会让框架复核落空，必须在 seed 阶段拦下。
        if (hasAnyAssertionField)
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                errors.Add(scope + " 声明了断言字段，但缺 toolName");
            }

            if (operatorValue is null)
            {
                errors.Add(scope + " 声明了断言字段，但缺 operator");
            }

            if (threshold is null)
            {
                errors.Add(scope + " 声明了断言字段，但缺 threshold");
            }

            if (string.IsNullOrWhiteSpace(seed.ValuePath))
            {
                errors.Add(scope + " 声明了断言字段，但缺 valuePath");
            }

            if (string.IsNullOrWhiteSpace(argumentsTemplate))
            {
                errors.Add(scope + " 声明了断言字段，但缺 arguments");
            }
        }

        if (operatorValue is not null && severity is null)
        {
            errors.Add(scope + " 是判定型检查项（有 operator），必须给 severity");
        }

        if (severity is not null && operatorValue is null)
        {
            errors.Add(scope + " 是信息型检查项（无 operator），不应给 severity");
        }

        if (seed.RuleVersion <= 0)
        {
            errors.Add(scope + " 的 ruleVersion 必须为正整数");
        }

        return new ChecklistItemSpec(
            sortOrder,
            title,
            seed.DataSourceHint,
            seed.Expectation,
            toolName,
            argumentsTemplate,
            seed.ValuePath,
            operatorValue,
            threshold,
            seed.Window,
            severity,
            seed.RuleVersion);
    }

    private static string? ReadThreshold(JsonElement? element, string scope, List<string> errors)
    {
        if (element is null || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return element.Value.ValueKind switch
        {
            JsonValueKind.String => element.Value.GetString()?.Trim(),
            JsonValueKind.Number => element.Value.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => element.Value.GetRawText(),
            _ => AddError(errors, $"{scope}.threshold 只能是字符串或数字"),
        };
    }

    private static string? ReadArguments(JsonElement? element, string scope, List<string> errors)
    {
        if (element is null || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (element.Value.ValueKind != JsonValueKind.Object)
        {
            return AddError(errors, $"{scope}.arguments 必须是 JSON 对象");
        }

        return JsonSerializer.Serialize(element.Value);
    }

    private static string? AddError(List<string> errors, string message)
    {
        errors.Add(message);
        return null;
    }

    private static string? RequireText(string? value, string scope, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(scope + " 不能为空");
            return null;
        }

        return value.Trim();
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback, string scope, List<string> errors)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (Enum.TryParse<TEnum>(value.Trim(), ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        errors.Add($"{scope} 取值非法：{value}（可选值：{string.Join(" / ", Enum.GetNames<TEnum>())}）");
        return fallback;
    }

    /// <summary>统一换行、去掉行尾空白，保证正文以单个换行结尾。</summary>
    private static string NormalizeMarkdown(string body)
    {
        var lines = body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(line => line.TrimEnd());
        return string.Join("\n", lines).Trim() + "\n";
    }
}
