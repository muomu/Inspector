using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace GrowthInspector.Management;

/// <summary>凭据掩码：读取返回掩码，更新传掩码或空值时保持原值（write-only 语义）。</summary>
internal static class SecretMask
{
    private const string Prefix = "****";

    public static string? Mask(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var suffix = value.Length <= 4 ? string.Empty : value[^4..];
        return Prefix + suffix;
    }

    public static bool IsMasked(string? value)
        => value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>空值或掩码值表示「保持原值」，其余按新值保存。</summary>
    public static string? Resolve(string? submitted, string? existing)
    {
        if (string.IsNullOrWhiteSpace(submitted) || IsMasked(submitted))
        {
            return existing;
        }

        return submitted.Trim();
    }
}

/// <summary>不透明分页游标：base64("offset:N")，只约束 limit / cursor，不暴露内部主键。</summary>
internal static class Cursor
{
    private const string Prefix = "offset:";

    public static int Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            if (text.StartsWith(Prefix, StringComparison.Ordinal)
                && int.TryParse(text[Prefix.Length..], out var offset)
                && offset >= 0)
            {
                return offset;
            }
        }
        catch (FormatException)
        {
            // 落到下面的统一错误。
        }

        throw new ManagementException("cursor 无效，请使用上一页返回的 nextCursor。");
    }

    public static string Encode(int offset)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Prefix}{offset}"));
}

/// <summary>输入校验与分页辅助。</summary>
internal static class ManagementRules
{
    public static int NormalizeLimit(int limit)
        => limit switch
        {
            <= 0 => 50,
            > 200 => 200,
            _ => limit,
        };

    public static async Task<PagedResult<T>> PageAsync<T>(
        IQueryable<T> query,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var offset = Cursor.Decode(cursor);
        var take = NormalizeLimit(limit);
        var items = await query.Skip(offset).Take(take + 1).ToListAsync(cancellationToken);
        var hasMore = items.Count > take;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        return new PagedResult<T>(items, hasMore ? Cursor.Encode(offset + take) : null);
    }

    public static string RequiredText(string? value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ManagementException($"{field}不能为空。");
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new ManagementException($"{field}长度不能超过 {maxLength} 个字符。");
        }

        return trimmed;
    }

    public static string? OptionalText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new ManagementException($"字段长度不能超过 {maxLength} 个字符。");
        }

        return trimmed;
    }

    /// <summary>JSON 列（手册正文除外）只接受合法 JSON；空串按 null 处理。</summary>
    public static string? OptionalJson(string? value, string field, JsonValueKind expectedKind)
    {
        var trimmed = OptionalText(value, 64 * 1024);
        if (trimmed is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind != expectedKind)
            {
                throw new ManagementException($"{field}必须是合法的 JSON {(expectedKind == JsonValueKind.Array ? "数组" : "对象")}。");
            }
        }
        catch (JsonException)
        {
            throw new ManagementException($"{field}必须是合法的 JSON {(expectedKind == JsonValueKind.Array ? "数组" : "对象")}。");
        }

        return trimmed;
    }
}
