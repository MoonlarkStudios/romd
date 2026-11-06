using System.Text.Json;
using System.Xml;
using ErrorOr;
using Romd.Admin.Application.Source.Dat;
using Romd.Application.Common.Pagination;

namespace Romd.Infrastructure.Dats;

public sealed partial class DatReplacementReview
{
    public async Task<ErrorOr<PagedList<DatEntryChange>>> ListChangesAsync(int? datId, string expectedName,
        Stream candidate, DatChangePageRequest page, CancellationToken ct)
    {
        var pair = await ReviewedPairAsync(datId, expectedName, candidate, page, ct);
        if (pair.IsError)
            return pair.Errors;
        var (before, after) = pair.Value;
        return page.Page(EntryChanges(before, after, ct).Where(item => page.Matches(item.Name, item.Change)), item => item.Name);
    }

    public async Task<ErrorOr<DatEntryChangeDetail>> GetEntryChangeAsync(int? datId, string expectedName,
        Stream candidate, string entryName, DatChangePageRequest page, CancellationToken ct)
    {
        var pair = await ReviewedPairAsync(datId, expectedName, candidate, page, ct);
        if (pair.IsError)
            return pair.Errors;
        var (before, after) = pair.Value;
        before.Entries.TryGetValue(entryName, out var old);
        after.Entries.TryGetValue(entryName, out var next);
        // The detail is a member of the change list, so an entry without a change is not found.
        if (EntryChange(entryName, old, next) is not { } change)
            return Error.NotFound("DatReview.Entry", "This entry has no change in the reviewed documents.");
        return new DatEntryChangeDetail(entryName, change.Change, Fields(old?.Metadata, next?.Metadata),
            page.Page(FileChanges(old, next, ct), file => $"{file.Kind}:{file.Name}"), before.Hash, after.Hash);
    }

    private async Task<ErrorOr<(Document Before, Document After)>> ReviewedPairAsync(int? datId, string expectedName,
        Stream candidate, DatChangePageRequest page, CancellationToken ct)
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
                after = await ParseAsync(candidate, ct);
                if (after.Name != expectedName)
                    return Error.Validation("DatReview.Identity", "This document does not match the selected catalog.");
                before = new([], "", expectedName, null, []);
            }
            catch (Exception ex) when (ex is InvalidDocumentException or XmlException)
            {
                return Error.Validation("DatReview.InvalidDocument", "The candidate could not be validated. Request a new preview.");
            }
        }
        if (before.Hash != page.ActiveSha256 || after.Hash != page.CandidateSha256)
            return Error.Conflict("DatReview.Stale", "The catalog or candidate changed. Request a new preview before continuing.");
        return (before, after);
    }

    // Files stream in ordinal kind:name key order, the order the file page cursor binds.
    private static IEnumerable<DatFileChange> FileChanges(Entry? old, Entry? next, CancellationToken ct)
    {
        foreach (string key in (old?.Files.Keys ?? Enumerable.Empty<string>()).Union(next?.Files.Keys ?? Enumerable.Empty<string>()).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            FileFact? prior = null, current = null;
            old?.Files.TryGetValue(key, out prior);
            next?.Files.TryGetValue(key, out current);
            if (prior?.Content == current?.Content)
                continue;
            string change = prior is null ? "Added" : current is null ? "Removed" : "Changed";
            int separator = key.IndexOf(':');
            yield return new(key[(separator + 1)..], key[..separator], change,
                prior is not null && current is not null && prior.Hashes != current.Hashes, Fields(prior?.Content, current?.Content));
        }
    }

    // Use the same comparison for summary counts and all pages; pagination cannot
    // silently select a different definition of "changed" from the review header.
    private static IEnumerable<DatEntryChange> EntryChanges(Document before, Document after, CancellationToken ct)
    {
        foreach (string name in before.Entries.Keys.Union(after.Entries.Keys).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            before.Entries.TryGetValue(name, out var old);
            after.Entries.TryGetValue(name, out var next);
            if (EntryChange(name, old, next) is { } change)
                yield return change;
        }
    }

    private static DatEntryChange? EntryChange(string name, Entry? old, Entry? next)
    {
        if (old is null)
            return next is null ? null : new(name, "Added", next.Files.Count, 0, 0);
        if (next is null)
            return new(name, "Removed", 0, old.Files.Count, 0);
        int added = next.Files.Keys.Count(k => !old.Files.ContainsKey(k));
        int removed = old.Files.Keys.Count(k => !next.Files.ContainsKey(k));
        int changed = next.Files.Count(file => old.Files.TryGetValue(file.Key, out var prior) && file.Value.Content != prior.Content);
        return added + removed + changed > 0 || old.Metadata != next.Metadata ? new(name, "Changed", added, removed, changed) : null;
    }

    private static IReadOnlyList<DatFieldChange> Fields(string? before, string? after)
    {
        // Values stay strings at the wire boundary, including full Int64 ROM sizes.
        static Dictionary<string, string?> Values(string? json)
        {
            if (json is null)
                return [];
            using var document = JsonDocument.Parse(json);
            return document.RootElement.EnumerateObject().ToDictionary(p => p.Name,
                p => p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.ToString(), StringComparer.Ordinal);
        }
        var old = Values(before);
        var next = Values(after);
        return old.Keys.Union(next.Keys).Order(StringComparer.Ordinal)
            .Where(key => old.GetValueOrDefault(key) != next.GetValueOrDefault(key))
            .Select(key => new DatFieldChange(key, old.GetValueOrDefault(key), next.GetValueOrDefault(key))).ToArray();
    }
}
