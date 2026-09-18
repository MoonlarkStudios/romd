using System.Text.Json;
using Romd.Admin.Application.Source.Dat;

namespace Romd.Infrastructure.Dats;

/// <summary>Projects available catalogs from the Go reader's authenticated index.</summary>
internal static class SignedDatCatalogIndex
{
    internal static IEnumerable<PublishedDatCatalog> Read(JsonElement index)
    {
        var definitions = index.GetProperty("definitions");
        var catalogs = definitions.GetProperty("catalogs");
        var downloads = index.GetProperty("downloads");
        // A system definition alone never advertises an available DAT.
        foreach (var entry in index.GetProperty("snapshot").GetProperty("catalogs").EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            if (!catalogs.TryGetProperty(entry.Name, out var definition)) continue;
            var provider = definition.GetProperty("provider").GetString();
            var representation = definition.GetProperty("representation").GetString();
            if ((provider, representation) is not (("redump", "discs") or ("no-intro", "standard"))) continue;
            var systemId = definition.GetProperty("systemId").GetString();
            if (systemId is null) throw new InvalidDataException("Missing catalog system.");
            var name = entry.Value.GetProperty("name").GetString();
            if (name != definition.GetProperty("expectedName").GetString()) throw new InvalidDataException("Catalog identity mismatch.");
            var artifact = entry.Value.GetProperty("artifact");
            // A failed first acquisition has no downloadable catalog to offer.
            if (artifact.ValueKind != JsonValueKind.Object) continue;
            string? hash = null;
            if (artifact.ValueKind == JsonValueKind.Object)
            {
                hash = artifact.GetProperty("sha256").GetString();
                var path = artifact.GetProperty("path").GetString();
                if (path is null || !downloads.TryGetProperty(path, out var download)
                    || download.GetProperty("sha256").GetString() != hash
                    || download.GetProperty("bytes").GetInt32() != artifact.GetProperty("bytes").GetInt32())
                    throw new InvalidDataException("Catalog artifact mismatch.");
            }
            var counts = entry.Value.GetProperty("counts");
            var changed = entry.Value.GetProperty("lastChanged");
            yield return new(entry.Name, systemId, name!, definition.GetProperty("provider").GetString()!,
                entry.Value.GetProperty("health").GetString()!, hash, counts.GetProperty("games").GetInt32(), counts.GetProperty("roms").GetInt32(),
                changed.ValueKind == JsonValueKind.Null ? null : changed.GetDateTimeOffset());
        }
    }
}
