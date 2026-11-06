using System.Security.Cryptography;

namespace Romd.Hosting.IntegrationTests.Vfs;

internal static class VfsProbeHash
{
    internal static async Task<byte[]> ReadAsync(Stream source, Action<long> progress, CancellationToken ct)
    {
        byte[] buffer = new byte[1 << 20];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytes = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int count = await source.ReadAsync(buffer, ct);
            ct.ThrowIfCancellationRequested();
            if (count == 0)
                return hash.GetHashAndReset();
            hash.AppendData(buffer, 0, count);
            bytes += count;
            progress(bytes);
        }
    }
}
