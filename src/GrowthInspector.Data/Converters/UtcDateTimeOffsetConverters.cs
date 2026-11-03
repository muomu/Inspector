using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GrowthInspector.Data.Converters;

/// <summary>
/// DateTimeOffset → UTC ticks（long）的转换器。
/// SQLite 不支持对 DateTimeOffset 做 ORDER BY，转成 long 后排序 / 比较都可用；
/// 本项目所有时间戳均以 UTC 写入，转换不丢信息（偏移量统一为 +00:00）。
/// </summary>
public sealed class UtcDateTimeOffsetToTicksConverter : ValueConverter<DateTimeOffset, long>
{
    public UtcDateTimeOffsetToTicksConverter()
        : base(
            value => value.UtcDateTime.Ticks,
            value => new DateTimeOffset(value, TimeSpan.Zero))
    {
    }
}

/// <summary>可空 DateTimeOffset 的同上转换。</summary>
public sealed class UtcNullableDateTimeOffsetToTicksConverter : ValueConverter<DateTimeOffset?, long?>
{
    public UtcNullableDateTimeOffsetToTicksConverter()
        : base(
            value => value == null ? null : value.Value.UtcDateTime.Ticks,
            value => value == null ? null : new DateTimeOffset(value.Value, TimeSpan.Zero))
    {
    }
}
