using System.Buffers.Binary;
using System.Security.Cryptography;
using Romd.Media.Rvz;

namespace Romd.Media.Nintendo;

/// <summary>Encoder-only ticket parsing and title-key derivation; no key values enter diagnostics.</summary>
internal sealed class WiiTicket
{
    public const int Bytes = 0x2A4;
    // Retail and Korean constants: Dolphin 771fb154059c5812d6a715a23226249cc42877a2, IOSC default handles.
    private static ReadOnlySpan<byte> StandardCommonKey => [0xEB, 0xE4, 0x2A, 0x22, 0x5E, 0x85, 0x93, 0xE4, 0x48, 0xD9, 0xC5, 0x45, 0x73, 0x81, 0xAA, 0xF7];
    private static ReadOnlySpan<byte> KoreanCommonKey => [0x63, 0xB8, 0x2B, 0xB4, 0xF4, 0x61, 0x4E, 0x2E, 0x13, 0xF2, 0xFE, 0xFB, 0xBA, 0x4C, 0x9B, 0x7E];

    private readonly byte[] _standard, _korean;

    public WiiTicket() : this(StandardCommonKey, KoreanCommonKey) { }

    public WiiTicket(ReadOnlySpan<byte> standard, ReadOnlySpan<byte> korean)
    {
        if (standard.Length != 16 || korean.Length != 16)
            throw new ArgumentException("Wii common keys must be 16 bytes.");
        _standard = standard.ToArray();
        _korean = korean.ToArray();
    }

    public static RvzEncodeResult.Failure? Validate(ReadOnlySpan<byte> ticket)
    {
        if (ticket.Length < Bytes)
            return new RvzEncodeResult.Failure(RvzEncodeFailureKind.InvalidSource, "Truncated Wii ticket.");
        if (BinaryPrimitives.ReadUInt32BigEndian(ticket) != 0x00010001)
            return new RvzEncodeResult.Failure(RvzEncodeFailureKind.UnsupportedMedia, "This Wii ticket signature layout is outside the profile.");
        if (ticket[0x1F1] > 1)
            return new RvzEncodeResult.Failure(RvzEncodeFailureKind.UnsupportedMedia, "This Wii common-key index is outside the profile.");
        return null;
    }

    public RvzEncodeResult.Failure? TryDecrypt(ReadOnlySpan<byte> ticket, Span<byte> titleKey)
    {
        if (titleKey.Length != 16)
            throw new ArgumentException("A Wii title-key destination must be 16 bytes.", nameof(titleKey));
        titleKey.Clear();
        if (Validate(ticket) is { } failure)
            return failure;
        Span<byte> iv = stackalloc byte[16];
        iv.Clear();
        ticket.Slice(0x1DC, 8).CopyTo(iv);
        using var aes = Aes.Create();
        aes.Key = ticket[0x1F1] == 0 ? _standard : _korean;
        aes.DecryptCbc(ticket.Slice(0x1BF, 16), iv, titleKey, PaddingMode.None);
        return null;
    }
}
