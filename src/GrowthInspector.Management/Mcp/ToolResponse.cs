using System.Text.Json;
using GrowthInspector.Core.Models;

namespace GrowthInspector.Management.Mcp;

/// <summary>管理 MCP 工具的统一返回：成功 <c>{ ok: true, data }</c>，失败 <c>{ ok: false, error }</c>。</summary>
internal static class ToolResponse
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Ok(object? data)
        => JsonSerializer.Serialize(new { ok = true, data }, JsonOptions);

    public static string Fail(string error)
        => JsonSerializer.Serialize(new { ok = false, error }, JsonOptions);

    public static async Task<string> RunAsync(Func<Task<object?>> action)
    {
        try
        {
            return Ok(await action());
        }
        catch (ManagementException ex)
        {
            return Fail(ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail($"管理操作失败：{ex.Message}");
        }
    }

    public static Task<string> RunAsync(Func<Task> action)
        => RunAsync(async () =>
        {
            await action();
            return null;
        });

    public static Guid ParseId(string? value, string field = "id")
        => Guid.TryParse(value, out var id)
            ? id
            : throw new ManagementException($"{field} 不是合法的 GUID。");

    public static Guid? ParseOptionalId(string? value, string field)
        => string.IsNullOrWhiteSpace(value) ? null : ParseId(value, field);

    public static TEnum ParseEnum<TEnum>(string? value, TEnum fallback)
        where TEnum : struct, Enum
        => string.IsNullOrWhiteSpace(value)
            ? fallback
            : Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                ? parsed
                : throw new ManagementException($"枚举值「{value}」无效，可选值：{string.Join(", ", Enum.GetNames<TEnum>())}。");

    public static TEnum? ParseOptionalEnum<TEnum>(string? value, string field)
        where TEnum : struct, Enum
        => string.IsNullOrWhiteSpace(value)
            ? null
            : Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                ? parsed
                : throw new ManagementException($"{field}「{value}」无效，可选值：{string.Join(", ", Enum.GetNames<TEnum>())}。");

    public static DateOnly? ParseOptionalDate(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : DateOnly.TryParse(value, out var date)
                ? date
                : throw new ManagementException($"日期「{value}」无效，应为 yyyy-MM-dd。");
}
