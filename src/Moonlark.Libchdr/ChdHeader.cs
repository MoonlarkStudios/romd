namespace Moonlark.Libchdr;

/// <summary>An immutable snapshot of a native CHD header; no native pointers are retained.</summary>
public readonly record struct ChdHeader
{
    private readonly ChdCodec _codec0;
    private readonly ChdCodec _codec1;
    private readonly ChdCodec _codec2;
    private readonly ChdCodec _codec3;

    internal ChdHeader(uint version, ulong logicalBytes, uint hunkBytes, uint unitBytes,
        uint hunkCount, ChdCodec codec0, ChdCodec codec1, ChdCodec codec2, ChdCodec codec3,
        ChdSha1 rawSha1, ChdSha1 overallSha1, ChdSha1 parentSha1, bool hasParent)
    {
        Version = version;
        LogicalBytes = logicalBytes;
        HunkBytes = hunkBytes;
        UnitBytes = unitBytes;
        HunkCount = hunkCount;
        _codec0 = codec0;
        _codec1 = codec1;
        _codec2 = codec2;
        _codec3 = codec3;
        RawSha1 = rawSha1;
        OverallSha1 = overallSha1;
        ParentSha1 = parentSha1;
        HasParent = hasParent;
    }

    /// <summary>The CHD format version.</summary>
    public uint Version { get; }

    /// <summary>The uncompressed logical byte length.</summary>
    public ulong LogicalBytes { get; }

    /// <summary>The full decode-buffer size required for each hunk, including the final hunk.</summary>
    public uint HunkBytes { get; }

    /// <summary>The number of bytes in one logical unit, such as a CD frame.</summary>
    public uint UnitBytes { get; }

    /// <summary>The number of addressable hunks.</summary>
    public uint HunkCount { get; }

    /// <summary>The stored raw-data SHA-1. Older CHD versions may omit it.</summary>
    public ChdSha1 RawSha1 { get; }

    /// <summary>The stored overall SHA-1, including checksummed metadata for versions four and five.</summary>
    public ChdSha1 OverallSha1 { get; }

    /// <summary>The stored parent SHA-1, or the zero value when absent. A v1–v4 hash counts only with the parent flag set,
    /// as MAME reads it.</summary>
    /// <remarks>A file with a parent cannot open (see <see cref="HasParent"/>), so on an open <see cref="ChdFile"/> this is
    /// always the zero value.</remarks>
    public ChdSha1 ParentSha1 { get; }

    /// <summary>Whether the header requires a parent, including legacy parent flags.</summary>
    /// <remarks>No parent can be supplied, so a file that requires one fails to open, normally with
    /// <see cref="ChdError.RequiresParent"/> (InvalidData when a v1–v4 parent flag names no parent hash); on an open
    /// <see cref="ChdFile"/> this is always false.</remarks>
    public bool HasParent { get; }

    /// <summary>Returns a codec slot without allocating a collection.</summary>
    /// <param name="slot">The zero-based slot, from zero through three.</param>
    /// <returns>The slot's codec: the stored FourCC for v5, or the v5 equivalent of a v1–v4 legacy value. Unknown
    /// numeric values are preserved.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The slot is outside zero through three.</exception>
    public ChdCodec GetCodec(int slot) => slot switch
    {
        0 => _codec0,
        1 => _codec1,
        2 => _codec2,
        3 => _codec3,
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "A CHD header has four codec slots."),
    };
}
