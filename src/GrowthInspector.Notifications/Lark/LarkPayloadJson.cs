using System.Text.Json;

namespace GrowthInspector.Notifications.Lark;

/// <summary>Lark 报文 JSON 序列化约定：camelCase，便于页面与验收直接读取。</summary>
internal static class LarkPayloadJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);
}
