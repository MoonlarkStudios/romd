namespace Romd.Admin.Application.Titles;

public interface ITitleLookupNameStore
{
    Task<TitleLookupNameSnapshot?> ReadAsync(int titleId, CancellationToken cancellationToken);

    /// <summary>Replaces names inside the caller's transaction after it locks the title revision.</summary>
    Task<TitleLookupNameSnapshot> StageReplacementAsync(int titleId, Guid currentRevision,
        IReadOnlyList<TitleLookupName> names, CancellationToken cancellationToken);
}

public sealed record TitleLookupName(string Name, string SearchDocument);

public sealed record TitleLookupNameSnapshot(Guid Revision, IReadOnlyList<string> Names);
