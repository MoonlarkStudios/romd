using System.Text;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Vfs;

public sealed class VfsProbeContentTests
{
    [Fact]
    public async Task CreateAsync_RepeatedCrcVector_ReportsDatOrderChecksum()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        string path = Path.Combine(Path.GetTempPath(), "romd-vfs-crc-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte[] bytes = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("123456789", 7737)));
            await File.WriteAllBytesAsync(path, bytes, ct);
            await using var source = await VfsProbeContent.CreateAsync(path, ct);
            source.Length.ShouldBe(69633);
            // Independently calculated with Python's standard-library zlib.crc32.
            Convert.ToHexString(source.Crc32).ShouldBe("0008652B");
        }
        finally { File.Delete(path); }
    }
}
