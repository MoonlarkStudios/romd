using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Romd.Contracts.Common.ReferenceCatalog;

/// <summary>Round-trips only supplied fields, including explicit nulls.</summary>
public sealed class ReferencePatchJsonConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeof(ReferencePatchDto).IsAssignableFrom(typeToConvert);
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(PatchConverter<>).MakeGenericType(typeToConvert))!;

    public static IEnumerable<PropertyInfo> EditableProperties(Type type) => type.GetProperties()
        .Where(property => property.SetMethod is not null && property.GetCustomAttribute<JsonIgnoreAttribute>() is null);

    private sealed class PatchConverter<T> : JsonConverter<T> where T : ReferencePatchDto, new()
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a patch object.");
            var fields = EditableProperties(typeof(T)).ToDictionary(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name));
            var result = new T();
            foreach (var field in document.RootElement.EnumerateObject())
            {
                if (!fields.TryGetValue(field.Name, out var property)) throw new JsonException("Unknown patch field: " + field.Name);
                if (result.Changes.ContainsKey(field.Name)) throw new JsonException("Duplicate patch field: " + field.Name);
                if (field.Value.ValueKind == JsonValueKind.Null && field.Name is "name" or "compactLabel" or "monochrome")
                    throw new JsonException("Use the override reset endpoint to inherit " + field.Name + ".");
                property.SetValue(result, field.Value.Deserialize(property.PropertyType, options));
            }
            return result;
        }
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.Changes, options);
    }
}
