using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Romd.Media.TestSupport;

public sealed class SyntheticWiiPartition
{
    public long Offset { get; }
    public long DataOffset { get; }
    public int Sectors { get; }
    public byte[] Ticket { get; }
    public byte[] ExpectedTitleKey { get; }
    public byte[] Plain { get; }
    public IReadOnlyList<HashOverride> Overrides { get; }

    internal SyntheticWiiPartition(long offset, long dataOffset, int sectors, byte[] ticket, byte[] titleKey,
        byte[] plain, IReadOnlyList<HashOverride> overrides)
    {
        Offset = offset;
        DataOffset = dataOffset;
        Sectors = sectors;
        Ticket = ticket;
        ExpectedTitleKey = titleKey;
        Plain = plain;
        Overrides = overrides;
    }
}
public sealed class SyntheticWiiIso
{
    public byte[] Iso { get; }
    public IReadOnlyList<SyntheticWiiPartition> Partitions { get; }
    internal SyntheticWiiIso(byte[] iso, IReadOnlyList<SyntheticWiiPartition> partitions) { Iso = iso; Partitions = partitions; }
}

/// <summary>Independent logical Wii ISO with a real partition directory and encrypted tickets/sectors.</summary>
public static class SyntheticWiiIsoBuilder
{
    public static SyntheticWiiIso Create(byte[] commonKey, byte keyIndex, int sectors = 130, int partitions = 1,
        bool poisonHashes = false, bool zeroDataAfterFirstRegion = false, bool oneDirectoryGroup = false) =>
        CreateCore(commonKey, keyIndex, sectors, partitions, poisonHashes, zeroDataAfterFirstRegion, oneDirectoryGroup, null);

    public static SyntheticWiiIso Create(WiiTicketVector vector, int sectors = 130, bool poisonHashes = false) =>
        CreateCore([], vector.Index, sectors, 1, poisonHashes, false, false, vector);

    private static SyntheticWiiIso CreateCore(byte[] commonKey, byte keyIndex, int sectors, int partitions,
        bool poisonHashes, bool zeroDataAfterFirstRegion, bool oneDirectoryGroup, WiiTicketVector? vector)
    {
        if ((vector is null && commonKey.Length != 16) || sectors < 1 || partitions is < 1 or > 4)
            throw new ArgumentException("Invalid synthetic Wii parameters.");
        const int metadata = 0x20000;
        int dataBytes = checked(sectors * ReferenceDisc.SectorSize);
        int length = checked(0x50000 + partitions * (metadata + dataBytes) + 103);
        byte[] iso = new byte[length];
        new Random(37).NextBytes(iso);
        "RMDE01"u8.CopyTo(iso);
        U32(iso, 0x18, 0x5D1C9EA3);
        U32(iso, 0x1C, 0);
        iso[0x60] = iso[0x61] = 0;
        Array.Clear(iso, 0x40000, 32);
        var expected = new List<SyntheticWiiPartition>(partitions);
        int offset = 0x50000;
        for (int part = 0; part < partitions; part++)
        {
            int group = oneDirectoryGroup ? 0 : part;
            int table = 0x40020 + group * 0x200;
            U32(iso, 0x40000 + group * 8, (uint)(oneDirectoryGroup ? partitions : 1));
            U32(iso, 0x40004 + group * 8, (uint)(table / 4));
            int entry = table + (oneDirectoryGroup ? part * 8 : 0);
            U32(iso, entry, (uint)(offset / 4));
            U32(iso, entry + 4, (uint)part);
            byte[] titleKey = vector?.ExpectedTitleKey.ToArray() ?? new byte[16], titleId = new byte[8];
            if (vector is null)
                new Random(71 + part).NextBytes(titleKey);
            new Random(83 + part).NextBytes(titleId);
            byte[] ticket = vector?.Ticket.ToArray() ?? CreateTicket(commonKey, keyIndex, titleId, titleKey);
            "Root-CA00000001-XS00000003"u8.CopyTo(ticket.AsSpan(0x140));
            ticket.CopyTo(iso, offset);
            U32(iso, offset + 0x2A4, 0x208);
            U32(iso, offset + 0x2A8, 0x2C0 / 4);
            U32(iso, offset + 0x2AC, 0x300);
            U32(iso, offset + 0x2B0, 0x500 / 4);
            U32(iso, offset + 0x2B4, 0x8000 / 4);
            U32(iso, offset + 0x2B8, metadata / 4);
            U32(iso, offset + 0x2BC, (uint)(dataBytes / 4));
            byte[] plain = new byte[checked(sectors * ReferenceDisc.SectorData)];
            new Random(97 + part).NextBytes(plain);
            "RMDE01"u8.CopyTo(plain);
            U32(plain, 0x18, 0x5D1C9EA3);
            U32(plain, 0x1C, 0);
            U32(plain, 0x420, 0x3000 / 4);
            U32(plain, 0x424, 0x1000 / 4);
            U32(plain, 0x428, 0x100 / 4);
            U32(plain, 0x42C, 0x100 / 4);
            Array.Clear(plain, 0x1000, 0x100);
            U32(plain, 0x1000, 0x01000000);
            U32(plain, 0x1008, 3);
            U32(plain, 0x100C, 1);
            U32(plain, 0x1010, 0x2000 / 4);
            U32(plain, 0x1014, 17);
            U32(plain, 0x1018, 10);
            U32(plain, 0x101C, 0x2100 / 4);
            U32(plain, 0x1020, 31);
            "\0boot.bin\0romd.bin\0"u8.CopyTo(plain.AsSpan(0x1024));
            GameCubeIsoFixture.AddPadding(plain, 0x3000, Math.Min(0x24000, plain.Length - 0x3000));
            if (zeroDataAfterFirstRegion && sectors > 64)
                Array.Clear(plain, 64 * ReferenceDisc.SectorData, plain.Length - 64 * ReferenceDisc.SectorData);
            var overrides = new List<HashOverride>();
            if (poisonHashes)
            {
                var random = new Random(109 + part);
                int[] pads = [0x26C, 0x320, 0x32C, 0x3E0, 0x3EC];
                for (int sector = 0; sector < sectors; sector++)
                {
                    for (int hash = 0; hash < 31; hash++)
                        AddOverride(overrides, random, sector, hash * 20);
                    foreach (int pad in pads)
                        AddOverride(overrides, random, sector, pad);
                    for (int hash = 0; hash < 8; hash++)
                    {
                        AddOverride(overrides, random, sector, 0x280 + hash * 20);
                        AddOverride(overrides, random, sector, 0x340 + hash * 20);
                    }
                }
            }
            // Structurally valid public TMD fields; signatures remain synthetic and are not authenticated.
            int tmd = offset + 0x2C0;
            Array.Clear(iso, tmd, 0x208);
            U32(iso, tmd, 0x00010001);
            "Root-CA00000001-CP00000004"u8.CopyTo(iso.AsSpan(tmd + 0x140));
            iso[tmd + 0x180] = 1;
            ticket.AsSpan(0x1DC, 8).CopyTo(iso.AsSpan(tmd + 0x18C));
            U32(iso, tmd + 0x194, 1);
            BinaryPrimitives.WriteUInt16BigEndian(iso.AsSpan(tmd + 0x1DE), 1);
            BinaryPrimitives.WriteUInt16BigEndian(iso.AsSpan(tmd + 0x1EA), 1);
            BinaryPrimitives.WriteUInt64BigEndian(iso.AsSpan(tmd + 0x1EC), (ulong)plain.Length);
            SHA1.HashData(iso.AsSpan(offset + 0x8000, 0x18000), iso.AsSpan(tmd + 0x1F4, 20));
            byte[] encrypted = ReferenceDisc.EncryptPartition(titleKey, plain, sectors, overrides);
            encrypted.CopyTo(iso, offset + metadata);
            expected.Add(new SyntheticWiiPartition(offset, offset + metadata, sectors, ticket, titleKey, plain, overrides));
            offset += metadata + dataBytes;
        }
        return new SyntheticWiiIso(iso, expected);
    }

    public static byte[] CreateTicket(byte[] commonKey, byte keyIndex, byte[] titleId, byte[] titleKey)
    {
        if (commonKey.Length != 16 || titleId.Length != 8 || titleKey.Length != 16)
            throw new ArgumentException("Invalid synthetic ticket parameters.");
        byte[] ticket = new byte[0x2A4], iv = new byte[16];
        U32(ticket, 0, 0x00010001);
        titleId.CopyTo(ticket, 0x1DC);
        titleId.CopyTo(iv, 0);
        ticket[0x1F1] = keyIndex;
        using var aes = Aes.Create();
        aes.Key = commonKey;
        aes.EncryptCbc(titleKey, iv, ticket.AsSpan(0x1BF, 16), PaddingMode.None);
        return ticket;
    }

    private static void AddOverride(List<HashOverride> list, Random random, int sector, int offset)
    {
        byte[] hash = new byte[20];
        random.NextBytes(hash);
        list.Add(new HashOverride(sector, offset, hash));
    }
    private static void U32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at), value);
}
