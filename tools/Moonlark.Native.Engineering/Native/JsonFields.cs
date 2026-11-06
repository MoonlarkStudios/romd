using System.Collections.Immutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Core;

namespace Moonlark.Native.Engineering.Native;

/// <summary>Strict JSON reading and stable writing for manifests, receipts and CMake File API replies.</summary>
internal static class JsonFields
{
    private static readonly JsonDocumentOptions Strict = new() { AllowDuplicateProperties = false };
    private static readonly JsonWriterOptions Indented = new() { Encoder = JavaScriptEncoder.Default, Indented = true };

    /// <summary>Parses a JSON object, rejecting duplicate properties; malformed input is a failure value.</summary>
    internal static Result<JsonObject> ParseObject(ReadOnlySpan<byte> utf8, string what)
    {
        try
        {
            return JsonNode.Parse(utf8, documentOptions: Strict) is JsonObject value ? value : new Failure(what + " must be a JSON object");
        }
        catch (JsonException exception)
        {
            return new Failure($"{what} is not valid JSON: {exception.Message}");
        }
    }

    internal static Result<JsonObject> ReadObject(string path, string what) => ParseObject(File.ReadAllBytes(path), what);

    internal static Result<string> RequireString(JsonObject owner, string name) =>
        owner[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : Missing(name);

    internal static Result<long> RequireInteger(JsonObject owner, string name)
    {
        if (owner[name] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
            return Missing(name);
        if (value.TryGetValue(out long number))
            return number;
        return value.TryGetValue(out int small) ? small : Missing(name);
    }

    /// <summary>Structural equality by canonical bytes: object order is ignored, value spelling is not.</summary>
    internal static bool SameCanonical(JsonNode? left, JsonNode? right) =>
        CanonicalJson.Serialize(left).AsSpan().SequenceEqual(CanonicalJson.Serialize(right));

    internal static Result<JsonObject> RequireObject(JsonObject owner, string name) =>
        owner[name] is JsonObject value ? value : Missing(name);

    internal static Result<ImmutableArray<string>> RequireStrings(JsonObject owner, string name)
    {
        if (owner[name] is not JsonArray array)
            return Missing(name);
        ImmutableArray<string>.Builder items = ImmutableArray.CreateBuilder<string>(array.Count);
        foreach (JsonNode? item in array)
        {
            if (item is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
                return Missing(name);
            items.Add(value.GetValue<string>());
        }
        return items.MoveToImmutable();
    }

    /// <summary>True only when every property value is a JSON string.</summary>
    internal static bool HasOnlyStrings(JsonObject value) =>
        value.All(property => property.Value is JsonValue item && item.GetValueKind() == JsonValueKind.String);

    internal static JsonArray Array(IEnumerable<string> items) => new([.. items.Select(item => (JsonNode)item)]);

    /// <summary>Indented, ASCII-escaped JSON with a trailing newline, optionally ordinally sorting every object.</summary>
    internal static byte[] Serialize(JsonNode node, bool sortKeys)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Indented))
            (sortKeys ? Sorted(node) : node)!.WriteTo(writer);
        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    internal static string Compact(JsonNode node) => Encoding.ASCII.GetString(CanonicalJson.Serialize(node));

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject value => new JsonObject(value.OrderBy(property => property.Key, StringComparer.Ordinal)
            .Select(property => KeyValuePair.Create(property.Key, Sorted(property.Value)))),
        JsonArray value => new JsonArray([.. value.Select(Sorted)]),
        _ => node?.DeepClone(),
    };

    private static Failure Missing(string name) => new($"Missing or invalid field: {name}");
}
