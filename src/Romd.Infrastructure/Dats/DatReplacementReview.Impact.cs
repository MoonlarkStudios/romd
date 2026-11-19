using System.Xml;
using ErrorOr;
using Romd.Admin.Application.Source.Dat;
using Romd.Domain.Hashing;

namespace Romd.Infrastructure.Dats;

public sealed partial class DatReplacementReview
{
    // Parameters are limited by the parsed document's file ceiling. Query the
    // candidate hashes as one set so a large review does not repeatedly scan ROMs.
    private sealed record ReviewMatchFacts(
        Dictionary<(string Entry, string Key), DatReviewRomLink> Installed,
        HashSet<int> InstalledRomIds,
        Dictionary<string, DatReviewTitleLink> TitleLinks,
        ILookup<Sha1, DatReviewStoredRom> Incoming,
        HashSet<Sha1> CandidateHashes);

    public async Task<ErrorOr<DatReplacementPreview>> PreviewWithImpactAsync(int? datId, string expectedName,
        Stream candidate, CancellationToken ct)
    {
        Document before, after;
        if (datId is int id)
        {
            var pair = await ReadAsync(id, candidate, ct);
            if (pair.IsError)
                return pair.Errors;
            (before, after) = pair.Value;
        }
        else
        {
            try
            {
                var pair = await ReadInitialAsync(expectedName, candidate, ct);
                if (pair.IsError)
                    return pair.Errors;
                (before, after) = pair.Value;
            }
            catch (Exception ex) when (ex is InvalidDocumentException or XmlException)
            {
                return Error.Validation("DatReview.InvalidDocument", ex is InvalidDocumentException
                    ? ex.Message : "The DAT contains malformed XML. No catalog has been imported.");
            }
        }

        var changed = EntryChanges(before, after, ct).ToArray();
        var facts = await ReadImpactFactsAsync(datId, after, changed, ct);
        var entries = new List<DatEntryChange>(changed.Length);
        HashSet<int> lost = [];
        HashSet<int> gained = [];
        int diskChanges = 0;
        foreach (var item in changed)
        {
            ct.ThrowIfCancellationRequested();
            var assessment = AssessEntry(item, before, after, facts);
            entries.Add(assessment.Entry);
            lost.UnionWith(assessment.Lost);
            gained.UnionWith(assessment.Gained);
            if (assessment.DiskChanged)
                diskChanges++;
        }
        return Compare(before, after, entries) with
        {
            Impact = new(
                entries.Count(item => item.Impact?.NeedsReview == true),
                entries.Count(item => item.Change == "Removed" && item.Impact is { StoredRoms: > 0 }),
                entries.Count(item => item.Change == "Removed" && item.Impact?.TitleLinkAtRisk == true),
                lost.Count, gained.Count,
                entries.Count(item => item.Impact is { Sha1ChangedFiles: > 0 }),
                entries.Sum(item => item.Impact?.Sha1ChangedFiles ?? 0),
                entries.Count(item => item.Impact?.MetadataOnly == true),
                entries.Count(item => item.Impact?.OtherChecksumOnly == true), diskChanges)
        };
    }

    private async Task<ReviewMatchFacts> ReadImpactFactsAsync(int? datId, Document candidate,
        IReadOnlyList<DatEntryChange> changes, CancellationToken ct)
    {
        if (changes.Count == 0)
            return new([], [], new(StringComparer.Ordinal), Array.Empty<DatReviewStoredRom>().ToLookup(x => x.Sha1), []);

        HashSet<Sha1> candidateHashes = new();
        HashSet<Sha1> changedEntryHashes = new();
        var changedNames = changes.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (name, entry) in candidate.Entries)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var (key, file) in entry.Files)
            {
                if (RomHash(key, file) is not { } hash)
                    continue;
                candidateHashes.Add(hash);
                if (changedNames.Contains(name))
                    changedEntryHashes.Add(hash);
            }
        }

        var installed = datId is int id ? await _repository.GetReviewRomLinksAsync(id, ct) : [];
        var links = datId is int baseline ? await _repository.GetReviewTitleLinksAsync(baseline, ct) : [];
        var incoming = changedEntryHashes.Count > 0
            ? await _repository.GetReviewStoredRomsAsync(changedEntryHashes.ToArray(), ct)
            : [];
        return new(
            installed.ToDictionary(link => (link.EntryName, "rom:" + link.FileName)),
            installed.Select(link => link.RomId).ToHashSet(),
            links.ToDictionary(link => link.EntryName, StringComparer.Ordinal),
            incoming.ToLookup(rom => rom.Sha1), candidateHashes);
    }

    private static Sha1? RomHash(string key, FileFact? file) =>
        key.StartsWith("rom:", StringComparison.Ordinal) && Sha1.TryParse(file?.Sha1, out var hash) && !hash.IsEmpty
            ? hash : null;

    private static (DatEntryChange Entry, HashSet<int> Lost, HashSet<int> Gained, bool DiskChanged) AssessEntry(
        DatEntryChange change, Document before, Document after, ReviewMatchFacts facts)
    {
        before.Entries.TryGetValue(change.Name, out var old);
        after.Entries.TryGetValue(change.Name, out var next);
        HashSet<int> stored = [];
        HashSet<int> lost = [];
        HashSet<int> gained = [];
        int sha1Changed = 0;
        bool changedStoredSha1 = false;
        bool diskChanged = false;
        bool checksumOnly = false;
        bool otherChange = false;

        foreach (string key in (old?.Files.Keys ?? Enumerable.Empty<string>()).Union(next?.Files.Keys ?? Enumerable.Empty<string>()))
        {
            FileFact? prior = null, incoming = null;
            old?.Files.TryGetValue(key, out prior);
            next?.Files.TryGetValue(key, out incoming);
            var installed = facts.Installed.GetValueOrDefault((change.Name, key));
            if (installed is not null)
            {
                stored.Add(installed.RomId);
                if (!facts.CandidateHashes.Contains(installed.Sha1))
                    lost.Add(installed.RomId);
            }

            var matches = RomHash(key, incoming) is { } sha1 ? facts.Incoming[sha1] : [];
            bool matchesAfter = false;
            foreach (var match in matches)
            {
                matchesAfter = true;
                stored.Add(match.RomId);
                if (!facts.InstalledRomIds.Contains(match.RomId))
                    gained.Add(match.RomId);
            }

            if (prior?.Content == incoming?.Content)
                continue;
            diskChanged |= key.StartsWith("disk:", StringComparison.Ordinal);
            if (prior is null || incoming is null)
            {
                otherChange = true;
                continue;
            }
            if (prior.Sha1 != incoming.Sha1)
            {
                sha1Changed++;
                changedStoredSha1 |= installed is not null || matchesAfter;
            }
            else if (prior.Hashes != incoming.Hashes)
            {
                checksumOnly = true;
                otherChange |= Fields(prior.Content, incoming.Content).Any(field => field.Field is not ("Crc" or "Md5"));
            }
            else
            {
                otherChange = true;
            }
        }

        var title = facts.TitleLinks.GetValueOrDefault(change.Name);
        bool titleAtRisk = title is not null &&
            ((next is null && !title.ReferencedByAnotherVersion) || (next?.Bios == true && old?.Bios == false));
        var impact = new DatEntryReviewImpact(stored.Count, lost.Count, gained.Count, title?.TitleName, titleAtRisk,
            sha1Changed, change is { Change: "Changed", FilesAdded: 0, FilesRemoved: 0, FilesChanged: 0 },
            checksumOnly && !otherChange && sha1Changed == 0 && old?.Metadata == next?.Metadata,
            titleAtRisk || (change.Change == "Removed" && stored.Count > 0) || lost.Count > 0 || changedStoredSha1,
            title?.TitleId, title?.OtherSourceLinks ?? 0);
        return (change with { Impact = impact }, lost, gained, diskChanged);
    }

    private static DatFileReviewImpact? FileImpact(string entryName, string key, FileFact? incoming, ReviewMatchFacts facts)
    {
        if (!key.StartsWith("rom:", StringComparison.Ordinal))
            return null;
        var installed = facts.Installed.GetValueOrDefault((entryName, key));
        var matches = RomHash(key, incoming) is { } sha1 ? facts.Incoming[sha1] : [];
        return new(installed is not null, matches.Any(),
            installed is not null && !facts.CandidateHashes.Contains(installed.Sha1),
            matches.Any(match => !facts.InstalledRomIds.Contains(match.RomId)));
    }
}
