using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class RequestBufferDirectoryLeaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"romd-buffer-lease-{Guid.NewGuid():N}");

    [Fact]
    public async Task StartingAsync_AbandonedBuffers_RemovesOnlyRegularMatchingFiles()
    {
        Directory.CreateDirectory(_directory);
        string orphan = Path.Combine(_directory, "ASPNETCORE_orphan.tmp");
        string other = Path.Combine(_directory, "other.tmp");
        string nested = Path.Combine(_directory, "nested");
        File.WriteAllText(orphan, "orphan");
        File.WriteAllText(other, "keep");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "ASPNETCORE_nested.tmp"), "keep");
        string link = Path.Combine(_directory, "ASPNETCORE_link.tmp");
        File.CreateSymbolicLink(link, other);

        using var owner = CreateOwner();
        await owner.StartingAsync(default);

        File.Exists(orphan).ShouldBeFalse();
        File.Exists(other).ShouldBeTrue();
        File.Exists(link).ShouldBeTrue();
        Directory.GetFiles(nested).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task StartingAsync_LiveOwnerAndStoppingOwner_ProtectsActiveFrameworkBufferUntilDisposed()
    {
        using var owner = CreateOwner();
        await owner.StartingAsync(default);
        using var input = new MemoryStream(new byte[1024]);
        using var buffer = new FileBufferingReadStream(input, 1, null, () => _directory);
        await buffer.CopyToAsync(Stream.Null);
        string path = buffer.TempFileName!;
        File.Exists(path).ShouldBeTrue();

        using var contender = CreateOwner();
        Should.Throw<InvalidOperationException>(() => contender.StartingAsync(default));
        File.Exists(path).ShouldBeTrue();
        await owner.StopAsync(default);
        Should.Throw<InvalidOperationException>(() => contender.StartingAsync(default));
        File.Exists(path).ShouldBeTrue();

        buffer.Dispose();
        owner.Dispose();
        await contender.StartingAsync(default);
        File.Exists(Path.Combine(_directory, ".romd-request-buffers.lock")).ShouldBeTrue();
    }

    [Fact]
    public async Task StartingAsync_NoConfiguredDirectory_DoesNotSweepOsTemp()
    {
        string sentinel = Path.Combine(Path.GetTempPath(), $"ASPNETCORE_{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(sentinel, "keep");
            using var owner = new RequestBufferDirectoryLease(null, NullLogger<RequestBufferDirectoryLease>.Instance);
            await owner.StartingAsync(default);
            File.Exists(sentinel).ShouldBeTrue();
        }
        finally
        {
            File.Delete(sentinel);
        }
    }

    private RequestBufferDirectoryLease CreateOwner() =>
        new(_directory, NullLogger<RequestBufferDirectoryLease>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
