using System.Text.Json;

namespace Romd.Persistence.ReferenceData;

/// <summary>Application-owned catalog generated from reference-data/catalog.</summary>
internal static class BundledReferenceData
{
    private static readonly Lazy<JsonElement> Snapshot = new(Load);
    internal static JsonElement Document => Snapshot.Value;
    internal static JsonElement Category(string name) => Document.GetProperty(name);
    private static JsonElement Load()
    {
        using var stream = typeof(BundledReferenceData).Assembly.GetManifestResourceStream("Romd.ReferenceData.json")
            ?? throw new InvalidDataException("Bundled reference data is missing.");
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidDataException("Unsupported reference schema.");
        return document.RootElement.Clone();
    }
}
