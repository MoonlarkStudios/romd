using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Application.Common.Configuration;
using Romd.Infrastructure.Diagnostics;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Diagnostics;

public sealed class StorageDiagnosticsReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"romd-diagnostics-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReadAsync_ExistingDataAndCas_ReturnsAvailableAndFreeSpace()
    {
        string cas = Path.Combine(_root, "content");
        Directory.CreateDirectory(cas);

        var result = await CreateReader(cas).ReadAsync();

        result.IsDataVolumeAvailable.ShouldBeTrue();
        result.DataVolumeFreeBytes.ShouldNotBeNull();
        result.DataVolumeFreeBytes.Value.ShouldBeGreaterThan(0);
        result.IsCasAvailable.ShouldBeTrue();
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task ReadAsync_MissingCas_ReturnsUnavailableWithoutCreatingIt()
    {
        Directory.CreateDirectory(_root);
        string cas = Path.Combine(_root, "missing-content");

        var result = await CreateReader(cas).ReadAsync();

        result.IsDataVolumeAvailable.ShouldBeTrue();
        result.IsCasAvailable.ShouldBeFalse();
        Directory.Exists(cas).ShouldBeFalse("diagnostics must never repair storage state");
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("does not exist");
    }

    [Fact]
    public async Task ReadAsync_InaccessibleCas_ReturnsCasUnavailable()
    {
        var probe = Substitute.For<IStorageDiagnosticsProbe>();
        probe.GetAvailableFreeSpace(_root).Returns(1234);
        probe.DirectoryExists(_root).Returns(true);
        string cas = Path.Combine(_root, "content");
        probe.DirectoryExists(cas).Returns(true);
        probe.When(candidate => candidate.AssertDirectoryReadable(cas))
            .Do(_ => throw new UnauthorizedAccessException("access denied"));

        var result = await CreateReader(cas, probe).ReadAsync();

        result.IsDataVolumeAvailable.ShouldBeTrue();
        result.IsCasAvailable.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("access denied");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private StorageDiagnosticsReader CreateReader(
        string casRoot,
        IStorageDiagnosticsProbe? probe = null)
    {
        var romdOptions = Substitute.For<IRomdOptions>();
        romdOptions.DataDirectory.Returns(_root);
        return new StorageDiagnosticsReader(
            romdOptions,
            Options.Create(new ContentStoreOptions { RootPath = casRoot }),
            probe ?? new FileSystemStorageDiagnosticsProbe());
    }
}
