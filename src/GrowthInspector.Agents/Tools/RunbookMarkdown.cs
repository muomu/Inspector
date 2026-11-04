namespace GrowthInspector.Agents.Tools;

/// <summary>手册中的一个 Markdown 章节（含标题行）。</summary>
public sealed record RunbookMarkdownSection(int Level, string Heading, string Content);

/// <summary>
/// 运维手册章节解析：只按 ATX 标题（<c>#</c> 到 <c>######</c>）切分，跳过围栏代码块内的伪标题。
/// 供 read_runbook 的渐进披露使用，不做完整的 Markdown 结构解析。
/// </summary>
public static class RunbookMarkdown
{
    /// <summary>解析出全部章节；章节内容包含自身的标题行及下属子章节，直到下一个同级或更高级标题。</summary>
    public static IReadOnlyList<RunbookMarkdownSection> ParseSections(string body)
    {
        var lines = NormalizeNewlines(body).Split('\n');
        var headings = new List<(int LineIndex, int Level, string Heading)>();

        var inFence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                continue;
            }

            if (TryParseHeading(line, out var level, out var heading))
            {
                headings.Add((i, level, heading));
            }
        }

        var sections = new List<RunbookMarkdownSection>(headings.Count);
        for (var index = 0; index < headings.Count; index++)
        {
            var current = headings[index];
            var endExclusive = lines.Length;
            for (var next = index + 1; next < headings.Count; next++)
            {
                if (headings[next].Level <= current.Level)
                {
                    endExclusive = headings[next].LineIndex;
                    break;
                }
            }

            var content = string.Join('\n', lines[current.LineIndex..endExclusive]).TrimEnd();
            sections.Add(new RunbookMarkdownSection(current.Level, current.Heading, content));
        }

        return sections;
    }

    /// <summary>取指定章节；章节名允许带或不带前导 <c>#</c>，按 OrdinalIgnoreCase 匹配。</summary>
    public static RunbookMarkdownSection? FindSection(string body, string section)
    {
        var normalized = NormalizeHeading(section);
        if (normalized.Length == 0)
        {
            return null;
        }

        return ParseSections(body)
            .FirstOrDefault(item => string.Equals(item.Heading, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>全部章节标题，供「章节不存在」时提示 agent。</summary>
    public static IReadOnlyList<string> ListHeadings(string body)
        => ParseSections(body).Select(item => item.Heading).ToList();

    private static bool TryParseHeading(string line, out int level, out string heading)
    {
        level = 0;
        heading = string.Empty;

        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '#')
        {
            return false;
        }

        var index = 0;
        while (index < trimmed.Length && trimmed[index] == '#')
        {
            index++;
        }

        if (index is < 1 or > 6)
        {
            return false;
        }

        if (index == trimmed.Length || char.IsWhiteSpace(trimmed[index]) is false)
        {
            // 兼容 "#标题" 这种不带空格的写法。
            if (index == trimmed.Length)
            {
                return false;
            }
        }

        var text = trimmed[index..].Trim();
        if (text.Length == 0)
        {
            return false;
        }

        level = index;
        heading = text;
        return true;
    }

    private static string NormalizeHeading(string section)
    {
        var trimmed = section.Trim();
        var index = 0;
        while (index < trimmed.Length && trimmed[index] == '#')
        {
            index++;
        }

        return trimmed[index..].Trim();
    }

    private static string NormalizeNewlines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
