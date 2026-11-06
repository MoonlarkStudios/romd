using Romd.Dat.Parsing.Models;

namespace Romd.Dat.Parsing;

/// <summary>
///     Infers the provenance of a DAT from its header, shared across formats.
/// </summary>
public static class DatProvenanceDetector
{
    public static DatProvenance Detect(string name, string? author, string? url)
    {
        string n = name.ToLowerInvariant();
        string a = author?.ToLowerInvariant() ?? string.Empty;
        string u = url?.ToLowerInvariant() ?? string.Empty;

        return (a, u, n) switch
        {
            _ when a.Contains("no-intro") || u.Contains("no-intro") => DatProvenance.NoIntro,
            _ when a.Contains("redump") || u.Contains("redump") => DatProvenance.Redump,
            _ when a.Contains("tosec") || n.Contains("tosec") => DatProvenance.Tosec,
            _ when n.Contains("mame") || n.StartsWith("mame") => DatProvenance.Mame,
            _ => DatProvenance.Unknown
        };
    }
}
