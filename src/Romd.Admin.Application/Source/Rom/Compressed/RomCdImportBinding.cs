using ErrorOr;
using Romd.Domain.Hashing;
using Romd.Domain.Paths;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom.Compressed;

/// <summary>One complete independently measured raw track projection; no filename or DAT identity is implied.</summary>
public sealed record RomCdImportTrack(int Number, RomLogicalIdentity Identity);
public sealed record CatalogCdFileRequirement(int Id, string Fingerprint, string Name, long Size, Sha1? Sha1, Md5? Md5, Crc32? Crc32, bool IsDisk);
public sealed record CatalogCdImportEntry(int ReleaseId, int TitleId, int PlatformId, string PlatformKey, string Fingerprint,
    IReadOnlyList<CatalogCdFileRequirement> Files);
public sealed record CatalogCdBinBinding(CatalogCdFileRequirement Requirement, RomCdImportTrack Track);

/// <summary>Complete catalog association, before any generated descriptor identity is known.</summary>
public sealed record RomCdImportBinding(int TitleId, int PlatformId, CatalogDescriptorRequirement Cue,
    IReadOnlyList<CatalogCdBinBinding> Bins, string PlatformKey);

/// <summary>Loads bounded complete entries in one read snapshot, never just matching BIN rows.
/// This association is advisory; publication must revalidate current catalog facts under its topology fence.</summary>
public interface IRomCdImportCatalogReader
{
    Task<IReadOnlyList<CatalogCdImportEntry>> FindAsync(IReadOnlyList<RomLogicalIdentity> tracks,
        IReadOnlyList<string> platformKeys, CancellationToken ct);
}

/// <summary>Pure full-identity binding for the one-file-per-track import projection. Formatting and preservation are separate.</summary>
public static class RomCdImportBindings
{
    public static ErrorOr<RomCdImportBinding> Select(IReadOnlyList<RomCdImportTrack> tracks,
        IReadOnlyList<CatalogCdImportEntry> entries, IReadOnlyList<string> platformKeys)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(platformKeys);
        var captured = tracks.ToArray();
        if (captured.Length is < 1 or > 99 || captured.Where((track, i) => track is null || track.Identity is null || track.Number != i + 1 ||
                track.Identity.Size is < 2352 or > 1058400000 || track.Identity.Size % 2352 != 0).Any() ||
            captured.Sum(track => track.Identity.Size) > 1058400000)
            return RomCdImportErrors.InvalidMeasurement;
        if (entries.Count > 256 || entries.Any(entry => entry.Files.Count > 116) || platformKeys.Count == 0 || platformKeys.Any(string.IsNullOrWhiteSpace))
            return RomCdImportErrors.UnsupportedLayout;
        var candidates = entries.Where(entry => entry is not null && platformKeys.Contains(entry.PlatformKey, StringComparer.Ordinal))
            .Select(entry => Bind(captured, entry)).OfType<Candidate>().ToArray();
        if (candidates.Length == 0)
            return RomCdImportErrors.NoCatalogMatch;
        if (candidates.Length != 1 || candidates[0].Bins is null)
            return RomCdImportErrors.AmbiguousCatalog;
        var selected = candidates[0];
        var entry = selected.Entry;
        var files = entry.Files.ToArray();
        var cues = files.Where(file => file.Name.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entry.ReleaseId <= 0 || entry.TitleId <= 0 || entry.PlatformId <= 0 || !Label(entry.Fingerprint) ||
            cues.Length != 1 || files.Length != captured.Length + 1 || files.Any(file => file.IsDisk || file.Id <= 0 || !Label(file.Fingerprint) ||
                file.Name.Length > 240 || file.Name.Contains('/') || !PortableRelativePath.IsSafe(file.Name)) ||
            files.Select(file => file.Id).Distinct().Count() != files.Length ||
            files.Select(file => file.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length ||
            cues[0].Size is < 1 or > 65536 || cues[0].Sha1 is not { IsEmpty: false } || cues[0].Md5 is not { IsEmpty: false } || cues[0].Crc32 is not { IsEmpty: false })
            return RomCdImportErrors.UnsupportedLayout;
        var cue = cues[0];
        return new RomCdImportBinding(entry.TitleId, entry.PlatformId,
            new(cue.Id, entry.ReleaseId, entry.Fingerprint, cue.Fingerprint, cue.Name, cue.Size, cue.Sha1!.Value, cue.Md5!.Value, cue.Crc32!.Value),
            Array.AsReadOnly(selected.Bins!), entry.PlatformKey);
    }

    private sealed record Candidate(CatalogCdImportEntry Entry, CatalogCdBinBinding[]? Bins);
    private static Candidate? Bind(IReadOnlyList<RomCdImportTrack> tracks, CatalogCdImportEntry entry)
    {
        if (entry.Files is null || entry.Files.Count is < 1 or > 116 || entry.Files.Any(file => file is null || file.Name is null))
            return null;
        var bins = entry.Files.Where(file => file.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (bins.Length != tracks.Count)
            return null;
        var matches = tracks.Select(track => bins.Where(file => file.Size == track.Identity.Size && file.Sha1 == track.Identity.Sha1 &&
            file.Md5 == track.Identity.Md5 && file.Crc32 == track.Identity.Crc32).ToArray()).ToArray();
        if (matches.Any(files => files.Length == 0))
            return null;
        if (matches.Any(files => files.Length != 1) || matches.Select(files => files[0].Id).Distinct().Count() != tracks.Count)
            return new(entry, null);
        return new(entry, tracks.Select((track, i) => new CatalogCdBinBinding(matches[i][0], track)).ToArray());
    }
    private static bool Label(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 120 && !value.Any(char.IsControl);
}
