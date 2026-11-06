using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Romd.Infrastructure.Storage;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class ContentStoreStartupCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "romd-startup-cleanup", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [Fact]
    public async Task StartAsync_AbandonedStagingFile_IsRemovedAndObjectsKept()
    {
        await using var provider = new ServiceCollection()
            .AddContentAddressableStorage(options => options.RootPath = _root)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IContentAddressableStore>();
        var stored = await store.StoreAsync(new MemoryStream(new byte[100_000]));
        // Writers stage in the store's staging directory, the only place cleanup looks.
        string staging = Path.Combine(_root, ".staging");
        Directory.CreateDirectory(staging);
        string abandoned = Path.Combine(staging, $".tmp-{Guid.NewGuid():N}");
        await File.WriteAllBytesAsync(abandoned, [1, 2, 3]);
        using var cleanup = new ContentStoreStartupCleanup(store, NullLogger<ContentStoreStartupCleanup>.Instance);

        await cleanup.StartAsync(CancellationToken.None);
        await cleanup.ExecuteTask!;

        File.Exists(abandoned).ShouldBeFalse();
        (await store.VerifyAsync(stored.Key)).ShouldBeTrue();
    }

    [Fact]
    public async Task StartAsync_CleanupIoFailure_CompletesWithoutFaultingTheHost()
    {
        var store = Substitute.For<IContentAddressableStore>();
        store.CleanupTemporaryFilesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("Disk unavailable."));
        using var cleanup = new ContentStoreStartupCleanup(store, NullLogger<ContentStoreStartupCleanup>.Instance);

        await cleanup.StartAsync(CancellationToken.None);
        await cleanup.ExecuteTask!;

        await store.Received(1).CleanupTemporaryFilesAsync(Arg.Any<CancellationToken>());
    }
}
