using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Moonlark.Native.Engineering.Core;

/// <summary>Compact, ordinally key-sorted, ASCII-only JSON used for recipe identities.</summary>
internal static class CanonicalJson
{
    private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.Default, Indented = false };

    internal static byte[] Serialize(JsonNode? node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Options))
            Write(writer, node);
        return stream.ToArray();
    }

    internal static string Sha256(JsonNode? node) => Digest.Sha256(Serialize(node));

    private static void Write(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject value:
                writer.WriteStartObject();
                foreach (KeyValuePair<string, JsonNode?> property in value.OrderBy(property => property.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonArray value:
                writer.WriteStartArray();
                foreach (JsonNode? item in value)
                    Write(writer, item);
                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }
}
