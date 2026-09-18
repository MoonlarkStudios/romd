using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Romd.Persistence.Converters;

public sealed class DateTimeOffsetToMillisecondsConverter
    : ValueConverter<DateTimeOffset, long>
{
    public DateTimeOffsetToMillisecondsConverter() : base(
        v => v.ToUnixTimeMilliseconds(),
        v => DateTimeOffset.FromUnixTimeMilliseconds(v))
    {
    }
}

public sealed class NullableDateTimeOffsetToMillisecondsConverter
    : ValueConverter<DateTimeOffset?, long?>
{
    public NullableDateTimeOffsetToMillisecondsConverter() : base(
        v => v.HasValue ? v.Value.ToUnixTimeMilliseconds() : null,
        v => v.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(v.Value) : null)
    {
    }
}
