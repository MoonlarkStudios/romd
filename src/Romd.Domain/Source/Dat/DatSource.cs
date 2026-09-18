namespace Romd.Domain.Source.Dat;

/// <summary>
///     Identity anchor for a DAT source. Versions (<see cref="DatFile" /> rows) come and go
///     across replacements; the source id is the stable identity they all share.
/// </summary>
public sealed class DatSource
{
    private DatSource(int id, DateTimeOffset createdAt)
    {
        Id = id;
        CreatedAt = createdAt;
    }

    public int Id { get; }
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    ///     Creates a new source anchor. The id is assigned by persistence.
    /// </summary>
    public static DatSource CreateNew() => new(0, DateTimeOffset.UtcNow);
}
