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

    public async Task<TitleLookupNameSnapshot> StageReplacementAsync(int titleId, Guid currentRevision,
        IReadOnlyList<TitleLookupName> names, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Replacing lookup names requires the caller's transaction.");

        var ordered = names.OrderBy(name => name.SearchDocument, StringComparer.Ordinal).ToArray();
        var rows = db.TitleLookupNames.Where(name => name.TitleId == titleId);
        var current = await rows.ToListAsync(cancellationToken);
        var displayed = ordered.Select(name => name.Name).ToArray();
        if (current.Count == ordered.Length && ordered.All(name => current.Any(row =>
                row.SearchDocument == name.SearchDocument && row.Name == name.Name)))
            return new(currentRevision, displayed);

        await rows.ExecuteDeleteAsync(cancellationToken);
        db.TitleLookupNames.AddRange(ordered.Select(name => new TitleLookupNameEntity
        {
            TitleId = titleId,
            Name = name.Name,
            SearchDocument = name.SearchDocument
        }));
        var revision = Guid.NewGuid();
        await db.Titles.Where(title => title.Id == titleId)
            .ExecuteUpdateAsync(update => update.SetProperty(title => title.Revision, revision), cancellationToken);
        return new(revision, displayed);
    }
}
