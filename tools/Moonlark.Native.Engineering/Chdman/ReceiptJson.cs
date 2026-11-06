using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Moonlark.Native.Engineering.Chdman;

/// <summary>
/// Receipt text byte-identical to Python's <c>json.dumps(value, indent=2) + "\n"</c> for ASCII content.
/// Non-ASCII text is written as UTF-8 rather than <c>\u</c> escapes; recorded receipts are ASCII.
/// </summary>
internal static class ReceiptJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static string Serialize(JsonNode node) => node.ToJsonString(Options) + "\n";
}
