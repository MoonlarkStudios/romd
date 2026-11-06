namespace Moonlark.Libchdr;

/// <summary>A CHD metadata FourCC in big-endian character order.</summary>
/// <param name="Value">The numeric tag. Zero is libchdr's wildcard.</param>
public readonly record struct ChdMetadataTag(uint Value)
{
    /// <summary>Matches metadata of any tag during enumeration.</summary>
    public static ChdMetadataTag Wildcard => default;

    /// <summary>Legacy CD track metadata, <c>CHTR</c>.</summary>
    public static ChdMetadataTag CdTrack => new(0x43485452);

    /// <summary>CD track metadata with explicit gaps, <c>CHT2</c>.</summary>
    public static ChdMetadataTag CdTrackV2 => new(0x43485432);

    /// <summary>GD-ROM track metadata, <c>CHGD</c>.</summary>
    public static ChdMetadataTag GdTrack => new(0x43484744);

    /// <summary>Legacy GD-ROM track metadata with little-endian audio, <c>CHGT</c>.</summary>
    public static ChdMetadataTag GdTrackLegacy => new(0x43484754);

    /// <summary>DVD metadata, <c>DVD </c>, including its trailing space.</summary>
    public static ChdMetadataTag Dvd => new(0x44564420);

    /// <summary>Creates a tag from exactly four printable ASCII characters.</summary>
    /// <param name="text">The four characters; spaces are significant.</param>
    /// <returns>The tag in CHD byte order.</returns>
    /// <exception cref="ArgumentException">The text is not four printable ASCII characters.</exception>
    public static ChdMetadataTag FromFourCC(ReadOnlySpan<char> text) => TryParse(text, out var tag)
        ? tag : throw new ArgumentException("A CHD metadata FourCC requires four printable ASCII characters.", nameof(text));

    /// <summary>Parses exactly four printable ASCII characters without allocating.</summary>
    /// <param name="text">The four characters; spaces are significant.</param>
    /// <param name="tag">The parsed tag, or the wildcard on failure.</param>
    /// <returns>Whether all four characters are valid.</returns>
    public static bool TryParse(ReadOnlySpan<char> text, out ChdMetadataTag tag)
    {
        tag = default;
        if (text.Length != 4)
            return false;
        uint value = 0;
        foreach (char character in text)
        {
            if (character is < ' ' or > '~')
                return false;
            value = (value << 8) | character;
        }
        tag = new(value);
        return true;
    }

    /// <summary>Formats a printable tag as four ASCII characters without allocating.</summary>
    /// <param name="destination">The destination, of at least four characters.</param>
    /// <param name="charsWritten">Four on success, zero on failure.</param>
    /// <returns>False for a short destination or a nonprintable numeric tag; the buffer is unchanged.</returns>
    public bool TryFormat(Span<char> destination, out int charsWritten)
    {
        charsWritten = 0;
        if (destination.Length < 4 || !IsPrintable(Value))
            return false;
        for (int index = 0; index < 4; index++)
            destination[index] = (char)((Value >> ((3 - index) * 8)) & 255);
        charsWritten = 4;
        return true;
    }

    /// <summary>Returns the four characters, or hexadecimal for nonprintable numeric tags.</summary>
    /// <returns>A display representation that preserves the numeric tag.</returns>
    public override string ToString() => IsPrintable(Value)
        ? string.Create(4, this, static (destination, tag) => tag.TryFormat(destination, out _))
        : $"0x{Value:x8}";

    private static bool IsPrintable(uint value)
    {
        for (int index = 0; index < 4; index++, value >>= 8)
            if ((value & 255) is < 32 or > 126)
                return false;
        return true;
    }
}
