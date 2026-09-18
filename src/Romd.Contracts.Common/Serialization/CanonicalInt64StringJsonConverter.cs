using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Romd.Contracts.Common.Serialization;

/// <summary>Serializes signed and unsigned 64-bit integers as canonical decimal JSON strings.</summary>
public sealed class CanonicalInt64StringJsonConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert == typeof(long) || typeToConvert == typeof(long?) ||
        typeToConvert == typeof(ulong) || typeToConvert == typeof(ulong?);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        typeToConvert switch
        {
            _ when typeToConvert == typeof(long) => new Int64Converter(),
            _ when typeToConvert == typeof(long?) => new NullableInt64Converter(),
            _ when typeToConvert == typeof(ulong) => new UInt64Converter(),
            _ => new NullableUInt64Converter()
        };

    private static long ReadCanonicalInt64(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Expected a canonical decimal Int64 string.");

        string? value = reader.GetString();
        if (value is null ||
            !long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long parsed) ||
            !string.Equals(value, parsed.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            throw new JsonException("Expected a canonical decimal Int64 string.");
        }

        return parsed;
    }

    private static ulong ReadCanonicalUInt64(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Expected a canonical decimal UInt64 string.");

        string? value = reader.GetString();
        if (value is null ||
            !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsed) ||
            !string.Equals(value, parsed.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            throw new JsonException("Expected a canonical decimal UInt64 string.");
        }

        return parsed;
    }

    private sealed class Int64Converter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            ReadCanonicalInt64(ref reader);

        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }

    private sealed class NullableInt64Converter : JsonConverter<long?>
    {
        public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? null : ReadCanonicalInt64(ref reader);

        public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
                writer.WriteStringValue(value.Value.ToString(CultureInfo.InvariantCulture));
            else
                writer.WriteNullValue();
        }
    }

    private sealed class UInt64Converter : JsonConverter<ulong>
    {
        public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            ReadCanonicalUInt64(ref reader);

        public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }

    private sealed class NullableUInt64Converter : JsonConverter<ulong?>
    {
        public override ulong? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? null : ReadCanonicalUInt64(ref reader);

        public override void Write(Utf8JsonWriter writer, ulong? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
                writer.WriteStringValue(value.Value.ToString(CultureInfo.InvariantCulture));
            else
                writer.WriteNullValue();
        }
    }
}
