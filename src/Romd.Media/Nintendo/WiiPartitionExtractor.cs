using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Romd.Storage;

namespace Romd.Media.Nintendo;

/// <summary>One bounded region of plaintext and byte-exact hash exceptions. Keys are never diagnostic data.</summary>
internal sealed class WiiPartitionExtractor : IDisposable
{
    public const int RegionBytes = 64 * WiiRegion.SectorSize, MaximumExceptions = 52 * 64;
    public const int MaximumListBytes = 2 + MaximumExceptions * 22;
    private readonly Aes _aes;
    private readonly byte[] _key, _encrypted, _plain, _hashes;
    private int _sectors, _disposed;

    public WiiPartitionExtractor(ReadOnlySpan<byte> key)
    {
        if (key.Length != 16)
            throw new ArgumentException("A Wii partition key must be 16 bytes.", nameof(key));
        _key = key.ToArray();
        Aes? aes = null;
        byte[]? encrypted = null, plain = null, hashes = null;
        try
        {
            aes = Aes.Create();
            aes.Key = _key;
            encrypted = ArrayPool<byte>.Shared.Rent(RegionBytes);
            plain = ArrayPool<byte>.Shared.Rent(RegionBytes);
            hashes = ArrayPool<byte>.Shared.Rent(64 * WiiRegion.HashBlock);
            _aes = aes;
            _encrypted = encrypted;
            _plain = plain;
            _hashes = hashes;
        }
        catch
        {
            aes?.Dispose();
            CryptographicOperations.ZeroMemory(_key);
            if (encrypted is not null)
                ArrayPool<byte>.Shared.Return(encrypted);
            if (plain is not null)
                ArrayPool<byte>.Shared.Return(plain);
            if (hashes is not null)
                ArrayPool<byte>.Shared.Return(hashes);
            throw;
        }
    }

    public async Task<bool> ReadRegionAsync(IRandomAccessContent source, long offset, int sectors, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (sectors is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(sectors));
        _sectors = 0;
        if (!await WiiSourceRead.FillAsync(source, offset, _encrypted.AsMemory(0, sectors * WiiRegion.SectorSize), ct))
            return false;
        Span<byte> iv = stackalloc byte[16];
        iv.Clear();
        for (int sector = 0; sector < sectors; sector++)
        {
            ct.ThrowIfCancellationRequested();
            var encrypted = _encrypted.AsSpan(sector * WiiRegion.SectorSize, WiiRegion.SectorSize);
            _aes.DecryptCbc(encrypted[..WiiRegion.HashBlock], iv, _hashes.AsSpan(sector * WiiRegion.HashBlock, WiiRegion.HashBlock), PaddingMode.None);
            _aes.DecryptCbc(encrypted[WiiRegion.HashBlock..], encrypted.Slice(0x3D0, 16),
                _plain.AsSpan(sector * WiiRegion.SectorSize + WiiRegion.HashBlock, WiiRegion.SectorData), PaddingMode.None);
        }
        WiiHashTree.Write(_plain.AsSpan(0, RegionBytes), sectors, ct);
        _sectors = sectors;
        return true;
    }

    public void CopyPayload(int firstSector, int sectors, Span<byte> target)
    {
        ValidateRange(firstSector, sectors);
        if (target.Length != sectors * WiiRegion.SectorData)
            throw new ArgumentException("Invalid Wii payload destination.", nameof(target));
        for (int i = 0; i < sectors; i++)
            _plain.AsSpan((firstSector + i) * WiiRegion.SectorSize + WiiRegion.HashBlock, WiiRegion.SectorData)
                .CopyTo(target.Slice(i * WiiRegion.SectorData, WiiRegion.SectorData));
    }

    public int WriteExceptions(int firstSector, int sectors, Span<byte> target)
    {
        ValidateRange(firstSector, sectors);
        if (target.Length < MaximumListBytes)
            throw new ArgumentException("Invalid Wii exception destination.", nameof(target));
        int count = 0;
        for (int i = 0; i < sectors; i++)
        {
            var original = _hashes.AsSpan((firstSector + i) * WiiRegion.HashBlock, WiiRegion.HashBlock);
            var expected = _plain.AsSpan((firstSector + i) * WiiRegion.SectorSize, WiiRegion.HashBlock);
            for (int hash = 0; hash < 31; hash++)
                AddException(original, expected, hash * 20, i, target, ref count);
            AddException(original, expected, 0x26C, i, target, ref count);
            for (int hash = 0; hash < 8; hash++)
                AddException(original, expected, 0x280 + hash * 20, i, target, ref count);
            AddException(original, expected, 0x320, i, target, ref count);
            AddException(original, expected, 0x32C, i, target, ref count);
            for (int hash = 0; hash < 8; hash++)
                AddException(original, expected, 0x340 + hash * 20, i, target, ref count);
            AddException(original, expected, 0x3E0, i, target, ref count);
            AddException(original, expected, 0x3EC, i, target, ref count);
        }
        BinaryPrimitives.WriteUInt16BigEndian(target, (ushort)count);
        return 2 + count * 22;
    }

    private static void AddException(ReadOnlySpan<byte> original, ReadOnlySpan<byte> expected, int at, int sector,
        Span<byte> target, ref int count)
    {
        if (original.Slice(at, 20).SequenceEqual(expected.Slice(at, 20)))
            return;
        int entry = 2 + count++ * 22;
        BinaryPrimitives.WriteUInt16BigEndian(target[entry..], checked((ushort)((sector << 10) + at)));
        original.Slice(at, 20).CopyTo(target[(entry + 2)..]);
    }

    private void ValidateRange(int first, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (first < 0 || count < 0 || first > _sectors || count > _sectors - first)
            throw new ArgumentOutOfRangeException(nameof(count));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _aes.Dispose();
        CryptographicOperations.ZeroMemory(_key);
        ArrayPool<byte>.Shared.Return(_encrypted);
        ArrayPool<byte>.Shared.Return(_plain);
        ArrayPool<byte>.Shared.Return(_hashes);
    }
}
