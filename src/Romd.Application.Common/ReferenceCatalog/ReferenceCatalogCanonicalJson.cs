using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Romd.Contracts.Common.ReferenceCatalog;

namespace Romd.Application.Common.ReferenceCatalog;

/// <summary>romd-reference-json-v1: UTF-16 ordinal object keys, preserved arrays, safe integers, literal valid UTF-8 Unicode.</summary>
public static class ReferenceCatalogCanonicalJson
{
    public static string Hash(JsonElement value) => Convert.ToHexStringLower(SHA256.HashData(Encode(value)));
    public static byte[] Encode(JsonElement value)
    {
        var text = new StringBuilder();
        Append(value, text);
        return new UTF8Encoding(false, true).GetBytes(text.ToString());
    }
    public static ReferenceCatalogDescriptorDto Describe(ReferenceCatalogDto catalog)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var descriptor = new ReferenceCatalogDescriptorDto(catalog.SchemaVersion, catalog.BuiltInVersion, "", catalog.Revision,
            Hash(JsonSerializer.SerializeToElement(catalog, options)), new(catalog.Systems.Count, catalog.Companies.Count,
                catalog.Regions.Count, catalog.Languages.Count, catalog.RatingBoards.Count, catalog.Ratings.Count));
        return descriptor with { Revision = Hash(JsonSerializer.SerializeToElement(descriptor, options)) };
    }
    private static void Append(JsonElement value, StringBuilder output)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                output.Append('{');
                var first = true;
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var member in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    if (!names.Add(member.Name))
                        throw new JsonException("Duplicate object member.");
                    if (!first)
                        output.Append(',');
                    first = false;
                    String(member.Name, output);
                    output.Append(':');
                    Append(member.Value, output);
                }
                output.Append('}');
                break;
            case JsonValueKind.Array:
                output.Append('[');
                first = true;
                foreach (var element in value.EnumerateArray())
                { if (!first) output.Append(','); first = false; Append(element, output); }
                output.Append(']');
                break;
            case JsonValueKind.String:
                String(value.GetString()!, output);
                break;
            case JsonValueKind.Number:
                if (!value.TryGetDecimal(out var number) || decimal.Truncate(number) != number || number is < -9007199254740991m or > 9007199254740991m)
                    throw new JsonException("Canonical catalog numbers must be safe integers.");
                output.Append(((long)number).ToString(CultureInfo.InvariantCulture));
                break;
            case JsonValueKind.True:
                output.Append("true");
                break;
            case JsonValueKind.False:
                output.Append("false");
                break;
            case JsonValueKind.Null:
                output.Append("null");
                break;
            default:
                throw new JsonException("Unsupported canonical catalog JSON value.");
        }
    }
    private static void String(string value, StringBuilder output)
    {
        output.Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '"':
                    output.Append("\\\"");
                    break;
                case '\\':
                    output.Append("\\\\");
                    break;
                case '\b':
                    output.Append("\\b");
                    break;
                case '\t':
                    output.Append("\\t");
                    break;
                case '\n':
                    output.Append("\\n");
                    break;
                case '\f':
                    output.Append("\\f");
                    break;
                case '\r':
                    output.Append("\\r");
                    break;
                default:
                    if (c < 0x20)
                        output.Append("\\u00").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    else if (char.IsHighSurrogate(c))
                    {
                        if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                            throw new JsonException("Unpaired surrogate.");
                        output.Append(c).Append(value[++i]);
                    }
                    else if (char.IsLowSurrogate(c))
                        throw new JsonException("Unpaired surrogate.");
                    else
                        output.Append(c);
                    break;
            }
        }
        output.Append('"');
    }
}
