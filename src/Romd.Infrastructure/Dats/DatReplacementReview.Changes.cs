using System.Text.Json;
using System.Xml;
using ErrorOr;
using Romd.Admin.Application.Source.Dat;

namespace Romd.Infrastructure.Dats;

public sealed partial class DatReplacementReview
{
    public const int ChangePageSize = 50;

    public async Task<ErrorOr<DatChangePage>> ChangesAsync(int? datId, string expectedName,
        Stream candidate, DatChangeQuery query, CancellationToken ct)
    {
        if (query.Offset < 0 || query.Offset > MaximumFiles || query.Search?.Length > 200
            || query.EntryName?.Length > 4096 || query.Change is not (null or "" or "Added" or "Removed" or "Changed")
            || !Hash(query.CandidateSha256) || (query.ActiveSha256 != "" && !Hash(query.ActiveSha256)))
            return Error.Validation("DatReview.Query", "Choose a valid change filter, search, page, and document fingerprints.");
        Document before, after;
        if (datId is int id)
        {
            var pair = await ReadAsync(id, candidate, ct);
            if (pair.IsError) return pair.Errors;
            (before, after) = pair.Value;
        }
        else
        {
            try
            {
                after = await ParseAsync(candidate, ct);
                if (after.Name != expectedName) return Error.Validation("DatReview.Identity", "This document does not match the selected catalog.");
                before = new([], "", expectedName, null, []);
            }
            catch (Exception ex) when (ex is InvalidDocumentException or XmlException)
            {
                return Error.Validation("DatReview.InvalidDocument", "The candidate could not be validated. Request a new preview.");
            }
        }
        if (before.Hash != query.ActiveSha256 || after.Hash != query.CandidateSha256)
            return Error.Conflict("DatReview.Stale", "The catalog or candidate changed. Request a new preview before continuing.");
        bool Matches(string name, string change) => (string.IsNullOrEmpty(query.Change) || query.Change == change)
            && (string.IsNullOrWhiteSpace(query.Search) || name.Contains(query.Search.Trim(), StringComparison.OrdinalIgnoreCase));
        if (query.EntryName is null)
        {
            int total = 0;
            var entries = new List<DatEntryChange>(ChangePageSize);
            foreach (var item in EntryChanges(before, after, ct))
                if (Matches(item.Name, item.Change))
                {
                    if (total >= query.Offset && entries.Count < ChangePageSize) entries.Add(item);
                    total++;
                }
            return new DatChangePage(before.Hash, after.Hash, query.Offset, ChangePageSize, total, entries, null, [], []);
        }
        before.Entries.TryGetValue(query.EntryName, out var old);
        after.Entries.TryGetValue(query.EntryName, out var next);
        if (old is null && next is null) return Error.NotFound("DatReview.Entry", "This entry does not exist in the reviewed documents.");
        var files = new List<DatFileChange>(ChangePageSize);
        int matching = 0;
        foreach (string key in (old?.Files.Keys ?? Enumerable.Empty<string>()).Union(next?.Files.Keys ?? Enumerable.Empty<string>()).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            FileFact? prior = null, current = null;
            old?.Files.TryGetValue(key, out prior);
            next?.Files.TryGetValue(key, out current);
            if (prior?.Content == current?.Content) continue;
            string change = prior is null ? "Added" : current is null ? "Removed" : "Changed";
            int separator = key.IndexOf(':');
            string name = key[(separator + 1)..];
            if (!Matches(name, change)) continue;
            if (matching >= query.Offset && files.Count < ChangePageSize)
                files.Add(new(name, key[..separator], change, prior is not null && current is not null && prior.Hashes != current.Hashes,
                    Fields(prior?.Content, current?.Content)));
            matching++;
        }
        return new DatChangePage(before.Hash, after.Hash, query.Offset, ChangePageSize, matching, [], query.EntryName,
            Fields(old?.Metadata, next?.Metadata), files);
    }

    private static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    // Use the same comparison for summary counts and all pages; pagination cannot
    // silently select a different definition of "changed" from the review header.
    private static IEnumerable<DatEntryChange> EntryChanges(Document before, Document after, CancellationToken ct)
    {
        foreach (string name in before.Entries.Keys.Union(after.Entries.Keys).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            before.Entries.TryGetValue(name, out var old);
            after.Entries.TryGetValue(name, out var next);
            if (old is null) { yield return new(name, "Added", next!.Files.Count, 0, 0); continue; }
            if (next is null) { yield return new(name, "Removed", 0, old.Files.Count, 0); continue; }
            int added = next.Files.Keys.Count(k => !old.Files.ContainsKey(k));
            int removed = old.Files.Keys.Count(k => !next.Files.ContainsKey(k));
            int changed = next.Files.Count(file => old.Files.TryGetValue(file.Key, out var prior) && file.Value.Content != prior.Content);
            if (added + removed + changed > 0 || old.Metadata != next.Metadata)
                yield return new(name, "Changed", added, removed, changed);
        }
    }

    private static IReadOnlyList<DatFieldChange> Fields(string? before, string? after)
    {
        // Values stay strings at the wire boundary, including full Int64 ROM sizes.
        static Dictionary<string, string?> Values(string? json)
        {
            if (json is null) return [];
            using var document = JsonDocument.Parse(json);
            return document.RootElement.EnumerateObject().ToDictionary(p => p.Name,
                p => p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.ToString(), StringComparer.Ordinal);
        }
        var old = Values(before); var next = Values(after);
        return old.Keys.Union(next.Keys).Order(StringComparer.Ordinal)
            .Where(key => old.GetValueOrDefault(key) != next.GetValueOrDefault(key))
            .Select(key => new DatFieldChange(key, old.GetValueOrDefault(key), next.GetValueOrDefault(key))).ToArray();
    }
}
