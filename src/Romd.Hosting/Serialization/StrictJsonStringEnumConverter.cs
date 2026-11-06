using System.Text.Json;
using System.Text.Json.Serialization;

namespace Romd.Hosting.Serialization;

/// <summary>
///     Serializes enums by their declared names and accepts only exact ordinal name matches.
///     Unlike <see cref="JsonStringEnumConverter"/>, this rejects case variants, comma-composed
///     values, and integers even when their numeric value maps to a declared member.
/// </summary>
public sealed class StrictJsonStringEnumConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        Type converterType = typeof(StrictEnumConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter)(Activator.CreateInstance(converterType)
            ?? throw new InvalidOperationException($"Could not create a converter for {typeToConvert}."));
    }

    private sealed class StrictEnumConverter<TEnum> : JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        private static readonly IReadOnlyDictionary<string, TEnum> Members =
            Enum.GetNames<TEnum>().ToDictionary(
                name => name,
                name => Enum.Parse<TEnum>(name),
                StringComparer.Ordinal);

        public override TEnum Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String &&
                reader.GetString() is { } name &&
                Members.TryGetValue(name, out TEnum value))
            {
                return value;
            }

            throw new JsonException(
                $"The JSON value is not a declared, exact-case {typeof(TEnum).Name} name.");
        }

        public override void Write(
            Utf8JsonWriter writer,
            TEnum value,
            JsonSerializerOptions options)
        {
            string? name = Enum.GetName(value);
            if (name is null)
                throw new JsonException($"{value} is not a declared {typeof(TEnum).Name} member.");

            writer.WriteStringValue(name);
        }
    }
}
