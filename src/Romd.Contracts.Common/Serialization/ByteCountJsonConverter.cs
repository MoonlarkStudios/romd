using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Romd.Contracts.Common.Models;

namespace Romd.Contracts.Common.Serialization;

/// <summary>Serializes <see cref="ByteCount" /> as a canonical nonnegative decimal JSON string.</summary>
public sealed class ByteCountJsonConverter : JsonConverter<ByteCount>
{
    public override ByteCount Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Expected a canonical nonnegative decimal byte-count string.");

        string? value = reader.GetString();
        if (value is null ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) ||
            !string.Equals(value, parsed.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            throw new JsonException("Expected a canonical nonnegative decimal byte-count string.");
        }

        return new ByteCount(parsed);
    }

    public override void Write(Utf8JsonWriter writer, ByteCount value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
