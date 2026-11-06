using System.Buffers.Binary;

namespace Romd.Media.TestSupport;

public sealed record WiiTicketVector(byte Index, byte[] Ticket, byte[] ExpectedTitleKey);

/// <summary>Fixed independent OpenSSL AES vectors from pinned Dolphin retail/Korean constants.</summary>
public static class WiiTicketVectors
{
    public static WiiTicketVector Get(byte index)
    {
        if (index > 1)
            throw new ArgumentOutOfRangeException(nameof(index));
        byte[] ticket = new byte[0x2A4], title = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(ticket, 0x00010001);
        ticket[0x1F1] = index;
        for (int i = 0; i < title.Length; i++)
            title[i] = (byte)(0x31 + i * 7 + index * 19);
        for (int i = 0; i < 8; i++)
            ticket[0x1DC + i] = (byte)(0x71 + i * 11 + index * 13);
        (index == 0 ? StandardCiphertext : KoreanCiphertext).CopyTo(ticket.AsSpan(0x1BF));
        return new WiiTicketVector(index, ticket, title);
    }

    private static ReadOnlySpan<byte> StandardCiphertext => [0xAD, 0x02, 0xF9, 0x21, 0x33, 0x65, 0xFD, 0xE6, 0xAA, 0x63, 0x22, 0x72, 0x6B, 0x27, 0xA8, 0x48];
    private static ReadOnlySpan<byte> KoreanCiphertext => [0x54, 0xB6, 0x89, 0x71, 0xC9, 0x2F, 0xEB, 0x92, 0x43, 0x12, 0xC8, 0x46, 0x1F, 0xCF, 0x64, 0xFA];
}
