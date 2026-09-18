namespace Romd.Domain.Catalog;

/// <summary>
///     Represents a canonical BIOS/firmware entry that groups BIOS DatGames across DAT revisions.
///     BIOS entries are platform-specific and matched by normalized name.
///     Unlike <see cref="Title" />, a BIOS entry carries no enrichment metadata: it exists purely to
///     give platform firmware a stable identity and to surface ownership ("which BIOS do we have").
/// </summary>
public sealed class Bios
{
    private Bios(int id, int platformId, string name, string normalizedName, DateTimeOffset createdAt)
    {
        Id = id;
        PlatformId = platformId;
        Name = name;
        NormalizedName = normalizedName;
        CreatedAt = createdAt;
    }

    public int Id { get; private set; }
    public int PlatformId { get; private set; }

    /// <summary>
    ///     Display name for the BIOS, taken from the first matched DatGame.
    /// </summary>
    public string Name { get; private set; }

    /// <summary>
    ///     Normalized name used to group BIOS DatGames across DAT revisions. Region and version
    ///     tokens are preserved so distinct firmware (e.g. USA vs Japan) stays distinct.
    /// </summary>
    public string NormalizedName { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    ///     Creates a new BIOS entry with validated invariants.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when name or normalizedName is null or whitespace.</exception>
    public static Bios CreateNew(int platformId, string name, string normalizedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedName);

        return new Bios(
            id: 0,
            platformId: platformId,
            name: name,
            normalizedName: normalizedName,
            createdAt: DateTimeOffset.UtcNow);
    }

    /// <summary>
    ///     Rehydrates a BIOS entry from persistence. Trusts that data is valid.
    /// </summary>
    internal static Bios Rehydrate(
        int id,
        int platformId,
        string name,
        string normalizedName,
        DateTimeOffset createdAt) =>
        new(id, platformId, name, normalizedName, createdAt);
}
