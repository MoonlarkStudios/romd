using Romd.Domain.Hashing;

namespace Romd.Domain.Source.Rom;

/// <summary>Exact CD file identity, independent of its storage and catalog preservation status.</summary>
public sealed record RomCdFileIdentity
{
    public RomCdFileIdentity(long size, Sha256 sha256, Sha1 sha1, Md5 md5, Crc32 crc32)
    {
        if (size is < 1 or > 1058400000 || sha256.IsEmpty || sha1.IsEmpty || md5.IsEmpty || crc32.IsEmpty)
            throw new ArgumentException("A CD member needs a bounded, complete exact file identity.");
        Size = size;
        Sha256 = sha256;
        Sha1 = sha1;
        Md5 = md5;
        Crc32 = crc32;
    }
    public long Size { get; }
    public Sha256 Sha256 { get; }
    public Sha1 Sha1 { get; }
    public Md5 Md5 { get; }
    public Crc32 Crc32 { get; }
}

public enum RomCdTrackKind { Mode1Raw, Mode2Raw, Audio }
public enum RomCdStoredRole { Cue, Sidecar }

/// <summary>Whole raw track projection: stored pregap included, CHD alignment excluded.</summary>
public sealed record RomCdTrack(int Number, long FirstFrame, int Frames, RomCdTrackKind Kind);

public abstract record RomCdFileMember
{
    protected RomCdFileMember(string path, RomCdFileIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        // Portable relative paths only; names are part of the captured manifest, not host paths.
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Any(c => char.IsControl(c) || "\\:*?\"<>|".Contains(c)) ||
            path.StartsWith('/') || path.Split('/').Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." ||
                part.EndsWith('.') || part.EndsWith(' ')))
            throw new ArgumentException("A member requires a portable relative path.", nameof(path));
        Path = path;
        Identity = identity;
    }
    public string Path { get; }
    public RomCdFileIdentity Identity { get; }
}

/// <summary>One original BIN can concatenate several tracks; a track is never implicitly a file.</summary>
public sealed record RomCdBinMember : RomCdFileMember
{
    public RomCdBinMember(string path, RomCdFileIdentity identity, RomCdFileIdentity reconstructedIdentity,
        IReadOnlyList<int> tracks) : base(path, identity)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (identity != reconstructedIdentity || tracks.Count is < 1 or > 99 || tracks.Any(n => n is < 1 or > 99) ||
            tracks.Zip(tracks.Skip(1)).Any(pair => pair.Second != pair.First + 1))
            throw new ArgumentException("BIN publication requires exact source/output equality and consecutive whole tracks.");
        ReconstructedIdentity = reconstructedIdentity;
        Tracks = Array.AsReadOnly(tracks.ToArray());
    }
    public RomCdFileIdentity ReconstructedIdentity { get; }
    public IReadOnlyList<int> Tracks { get; }
}

/// <summary>Descriptors and sidecars retain their exact immutable bytes. Generated descriptors require an explicit catalog binding; track facts alone never prove original text.</summary>
public sealed record RomCdStoredMember : RomCdFileMember
{
    public RomCdStoredMember(string path, RomCdFileIdentity identity, RomCdStoredRole role) : base(path, identity)
    {
        if (!Enum.IsDefined(role) || identity.Size > (16L << 20))
            throw new ArgumentException("Stored CD descriptors/sidecars must be bounded exact files.");
        Role = role;
    }
    public RomCdStoredRole Role { get; }
}

