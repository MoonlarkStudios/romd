using System.Security.Cryptography;

namespace Romd.Hosting.IntegrationTests.Vfs;

internal sealed class VfsProbeContent : IAsyncDisposable
{
    private VfsProbeContent(Stream stream, string name, string kind)
    {
        Stream = stream;
        Name = name;
        Kind = kind;
    }

    public Stream Stream { get; }
    public long Length => Stream.Length;
    public string Name { get; }
    public string Kind { get; }
    public byte[] Sha256 { get; private set; } = [];
    public byte[] Sha1 { get; private set; } = [];
    public byte[] Md5 { get; private set; } = [];
    public byte[] Crc32 { get; private set; } = [];

    public static Task<VfsProbeContent> CreateAsync(CancellationToken ct = default)
    {
        string? path = Environment.GetEnvironmentVariable("ROMD_VFS_PROBE_FILE");
        VfsProbeContent source;
        if (!string.IsNullOrEmpty(path))
            source = new(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read), Path.GetFileName(path), "user-provided file");
        else
        {
            int mib = int.Parse(Environment.GetEnvironmentVariable("ROMD_VFS_PROBE_MIB") ?? "4", System.Globalization.CultureInfo.InvariantCulture);
            if (mib is < 1 or > 256)
                throw new ArgumentOutOfRangeException(nameof(mib));
            byte[] bytes = new byte[mib * 1024 * 1024];
            new Random(1729).NextBytes(bytes);
            source = new(new MemoryStream(bytes, writable: false), "track 100% #1.bin", "synthetic");
        }
        return MeasureAsync(source, ct);
    }

    public static Task<VfsProbeContent> CreateAsync(string path, CancellationToken ct = default) =>
        MeasureAsync(new VfsProbeContent(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read),
            Path.GetFileName(path), "user-provided file"), ct);

    private static async Task<VfsProbeContent> MeasureAsync(VfsProbeContent source, CancellationToken ct)
    {
        try
        {
            if (source.Length < 69626)
                throw new ArgumentException("Probe content must contain at least 69,626 bytes.");
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            var crc32 = new System.IO.Hashing.Crc32();
            byte[] buffer = new byte[128 * 1024];
            int count;
            while ((count = await source.Stream.ReadAsync(buffer, ct)) != 0)
            {
                sha256.AppendData(buffer, 0, count);
                sha1.AppendData(buffer, 0, count);
                md5.AppendData(buffer, 0, count);
                crc32.Append(buffer.AsSpan(0, count));
            }
            source.Sha256 = sha256.GetHashAndReset();
            source.Sha1 = sha1.GetHashAndReset();
            source.Md5 = md5.GetHashAndReset();
            source.Crc32 = Romd.Domain.Hashing.Crc32.FromLittleEndian(crc32.GetCurrentHash()).ToArray();
            source.Stream.Position = 0;
            return source;
        }
        catch { await source.DisposeAsync(); throw; }
    }

    public long NextOffset(Random random, int length) => Length <= int.MaxValue
        ? random.Next((int)Length - length)
        : random.NextInt64(Length - length);

    public byte[] ReadRange(long offset, int length)
    {
        byte[] bytes = new byte[length];
        Stream.Position = offset;
        Stream.ReadExactly(bytes);
        return bytes;
    }

    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}
