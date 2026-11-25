using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Titles;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

public sealed class TitleLookupNameStore(RomdDbContext db) : ITitleLookupNameStore
{
    public Task<TitleLookupNameSnapshot?> ReadAsync(int titleId, CancellationToken cancellationToken) =>
        db.Titles.Where(title => title.Id == titleId)
            .Select(title => new TitleLookupNameSnapshot(title.Revision, db.TitleLookupNames.Where(name => name.TitleId == title.Id)
                .OrderBy(name => name.SearchDocument).Select(name => name.Name).ToArray()))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<int> CountAsync(int titleId, CancellationToken cancellationToken) =>
        db.TitleLookupNames.CountAsync(name => name.TitleId == titleId, cancellationToken);

    public async Task<TitleLookupNameSnapshot> StageReplacementAsync(int titleId, Guid currentRevision,
        IReadOnlyList<TitleLookupName> names, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Replacing lookup names requires the caller's transaction.");

        var ordered = names.OrderBy(name => name.SearchDocument, StringComparer.Ordinal).ToArray();
        var rows = db.TitleLookupNames.Where(name => name.TitleId == titleId);
        var current = await rows.AsTracking().ToDictionaryAsync(row => row.SearchDocument, StringComparer.Ordinal, cancellationToken);
        var displayed = ordered.Select(name => name.Name).ToArray();
        if (current.Count == ordered.Length && ordered.All(name => current.GetValueOrDefault(name.SearchDocument)?.Name == name.Name))
            return new(currentRevision, displayed);

        foreach (var name in ordered)
        {
            if (current.Remove(name.SearchDocument, out var existing))
                existing.Name = name.Name;
            else
                db.TitleLookupNames.Add(new TitleLookupNameEntity { TitleId = titleId, Name = name.Name, SearchDocument = name.SearchDocument });
        }
        db.TitleLookupNames.RemoveRange(current.Values);
        var revision = Guid.NewGuid();
        await db.Titles.Where(title => title.Id == titleId)
            .ExecuteUpdateAsync(update => update.SetProperty(title => title.Revision, revision), cancellationToken);
        return new(revision, displayed);
    }
}
