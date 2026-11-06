using System.Security.Cryptography;

namespace Romd.Media.TestSupport;

/// <summary>
/// Deliberately naive reference implementations written from the specifications (Dolphin's
/// docs/WiaAndRvz.md and WiiBrew's Wii disc layout), independent of Romd.Media's optimized code.
/// </summary>
public static class ReferenceDisc
{
    public const int SectorSize = 0x8000, SectorData = 0x7C00, HashBlock = 0x400, RegionSectors = 64;

    /// <summary>Padding bytes for a run starting <paramref name="skip"/> bytes into a seeded stream.</summary>
    public static byte[] Padding(byte[] seed, int skip, int length)
    {
        var buffer = new uint[521];
        for (int i = 0; i < 17; i++)
            buffer[i] = (uint)(seed[i * 4] << 24 | seed[i * 4 + 1] << 16 | seed[i * 4 + 2] << 8 | seed[i * 4 + 3]);
        for (int i = 17; i < 521; i++)
            buffer[i] = (buffer[i - 17] << 23) ^ (buffer[i - 16] >> 9) ^ buffer[i - 1];
        for (int i = 0; i < 4; i++)
            Advance(buffer);
        var output = new byte[skip + length];
        int word = 0;
        for (int at = 0; at < output.Length; at += 4)
        {
            if (word == 521)
            { Advance(buffer); word = 0; }
            uint value = buffer[word++];
            byte[] bytes = [(byte)(value >> 24), (byte)(value >> 18), (byte)(value >> 8), (byte)value];
            for (int b = 0; b < 4 && at + b < output.Length; b++)
                output[at + b] = bytes[b];
        }
        return output.AsSpan(skip, length).ToArray();
    }

    private static void Advance(uint[] buffer)
    {
        for (int i = 0; i < 32; i++)
            buffer[i] ^= buffer[i + 521 - 32];
        for (int i = 32; i < 521; i++)
            buffer[i] ^= buffer[i - 32];
    }

    /// <summary>Encrypts hash-free partition data: hash tree, exception overrides, then AES-128-CBC.</summary>
    public static byte[] EncryptPartition(byte[] key, byte[] plain, int sectors, IReadOnlyList<HashOverride> overrides)
    {
        var output = new byte[(long)sectors * SectorSize];
        for (int regionStart = 0; regionStart < sectors; regionStart += RegionSectors)
        {
            var h0 = new byte[RegionSectors][];
            for (int s = 0; s < RegionSectors; s++)
            {
                h0[s] = new byte[31 * 20];
                for (int b = 0; b < 31; b++)
                {
                    int sector = regionStart + s;
                    var block = new byte[0x400];
                    if (sector < sectors)
                        Array.Copy(plain, (long)sector * SectorData + b * 0x400, block, 0, 0x400);
                    Sha1(block).CopyTo(h0[s], b * 20);
                }
            }
            var h1 = new byte[8][];
            var h2 = new byte[8 * 20];
            for (int g = 0; g < 8; g++)
            {
                h1[g] = new byte[8 * 20];
                for (int j = 0; j < 8; j++)
                    Sha1(h0[g * 8 + j]).CopyTo(h1[g], j * 20);
                Sha1(h1[g]).CopyTo(h2, g * 20);
            }
            for (int s = 0; s < RegionSectors && regionStart + s < sectors; s++)
            {
                int sector = regionStart + s;
                var hashBlock = new byte[HashBlock];
                h0[s].CopyTo(hashBlock, 0);
                h1[s / 8].CopyTo(hashBlock, 0x280);
                h2.CopyTo(hashBlock, 0x340);
                foreach (var hash in overrides.Where(item => item.Sector == sector))
                    hash.Hash.CopyTo(hashBlock, hash.Offset);
                byte[] encryptedHash = Transform(key, new byte[16], hashBlock);
                byte[] data = plain.AsSpan((int)((long)sector * SectorData), SectorData).ToArray();
                byte[] encryptedData = Transform(key, encryptedHash.AsSpan(0x3D0, 16).ToArray(), data);
                encryptedHash.CopyTo(output, (long)sector * SectorSize);
                encryptedData.CopyTo(output, (long)sector * SectorSize + HashBlock);
            }
        }
        return output;
    }

    private static byte[] Sha1(byte[] data)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(data);
        return hash.GetHashAndReset();
    }

    private static byte[] Transform(byte[] key, byte[] iv, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor(key, iv);
        var output = new byte[data.Length];
        encryptor.TransformBlock(data, 0, data.Length, output, 0);
        return output;
    }
}

/// <summary>Replaces 20 bytes of a partition sector's 0x400-byte hash block before encryption.</summary>
public sealed record HashOverride(int Sector, int Offset, byte[] Hash);
