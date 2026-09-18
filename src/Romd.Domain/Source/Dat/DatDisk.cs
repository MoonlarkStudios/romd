using Romd.Domain.Hashing;

namespace Romd.Domain.Source.Dat;

/// <summary>
///     Represents a disk (CHD) entry within a DAT game.
/// </summary>
public sealed class DatDisk
{
    private DatDisk(
        int id,
        int datGameId,
        string name,
        Sha1? sha1,
        Md5? md5,
        string? status)
    {
        Id = id;
        DatGameId = datGameId;
        Name = name;
        Sha1 = sha1;
        Md5 = md5;
        Status = status;
    }

    public int Id { get; private set; }
    public int DatGameId { get; private set; }

    public string Name { get; private set; }

    // Hashes
    public Sha1? Sha1 { get; private set; }
    public Md5? Md5 { get; private set; }

    // Status
    public string? Status { get; private set; }

    /// <summary>
    ///     Creates a new disk entry with validated invariants.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when name is null or whitespace.</exception>
    public static DatDisk CreateNew(
        string name,
        Sha1? sha1 = null,
        Md5? md5 = null,
        string? status = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new DatDisk(
            0,
            0,
            name,
            sha1,
            md5,
            status);
    }

    /// <summary>
    ///     Rehydrates a disk entry from persistence. Trusts that data is valid.
    /// </summary>
    internal static DatDisk Rehydrate(
        int id,
        int datGameId,
        string name,
        Sha1? sha1,
        Md5? md5,
        string? status) =>
        new(id, datGameId, name, sha1, md5, status);
}
