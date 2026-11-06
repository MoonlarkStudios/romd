using Romd.Media.Probe;
using NSubstitute;
using Romd.Application.Common.Configuration;
using Romd.Infrastructure.Storage;
using System.IO.Abstractions;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class SupervisorProbeEvidenceTests
{
    [Fact]
    public async Task InspectionCleanup_ChildUseHold_PreservesActualSourceAndAccounting()
    {
        string directory = Directory.CreateTempSubdirectory("romd-inspection-custody-").FullName;
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(directory);
        options.MaxUploadBytes.Returns(8);
        var staging = new LocalTempFileFactory(options, new FileSystem(), new TempStorageOptions
        {
            MaximumFiles = 1,
            MaximumBytes = 8,
            MinimumFreeBytes = 0,
            AdmissionTimeoutSeconds = 1
        });
        await using var source = await staging.CreateAsync(new MemoryStream([1, 2, 3, 4]), CancellationToken.None);
        string accounting = Path.Combine(directory, "temp", "staging-v1", "reservations.json");
        string reservation = await File.ReadAllTextAsync(accounting, CancellationToken.None);
        source.TryBeginChildUse().ShouldBeTrue();
        try
        {
            InspectionChecks.CleanupStaging(directory).ShouldBeFalse("unconfirmed child custody must preserve staging");
            File.ReadAllBytes(source.Path).ShouldBe(new byte[] { 1, 2, 3, 4 });
            File.Exists(source.Path + ".quarantine").ShouldBeTrue();
            (await File.ReadAllTextAsync(accounting, CancellationToken.None)).ShouldBe(reservation);
        }
        finally
        {
            if (Directory.Exists(directory))
                source.TryEndChildUse().ShouldBeTrue();
            await source.DisposeAsync();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InspectionCleanup_NoChildHold_RemovesOwnedDirectory()
    {
        string directory = Directory.CreateTempSubdirectory("romd-inspection-clean-").FullName;
        File.WriteAllText(Path.Combine(directory, "witness"), "owned disposable witness");
        InspectionChecks.CleanupStaging(directory).ShouldBeTrue();
        Directory.Exists(directory).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreserveFailureAsync_ReportSinkFailsOrSucceeds_PreservesPrimaryException(bool sinkFails)
    {
        var primary = new InvalidOperationException("primary qualification failure");
        var secondary = new IOException("secondary report failure");
        using var cancellation = new CancellationTokenSource();
        int writes = 0;
        Exception? observed = null;

        try
        {
            await SupervisorChecks.PreserveFailureAsync(primary, token =>
            {
                token.ShouldBe(cancellation.Token);
                writes++;
                return sinkFails ? Task.FromException(secondary) : Task.CompletedTask;
            }, cancellation.Token);
        }
        catch (Exception error) { observed = error; }

        writes.ShouldBe(1);
        observed.ShouldBeSameAs(primary);
    }

    [Fact]
    public async Task AssertStoppedAsync_MissingDescendant_PreservesAllFailureWitnesses()
    {
        string directory = Directory.CreateTempSubdirectory("romd-probe-witness-").FullName;
        string source = Path.Combine(directory, "descendant-exit.rvz");
        var witnesses = new Dictionary<string, string>
        {
            [source] = "immutable fixture",
            [source + ".pid"] = int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [source + ".native-probe"] = "native witness",
            [source + ".managed-probe"] = "managed witness"
        };
        try
        {
            foreach (var (path, content) in witnesses)
                await File.WriteAllTextAsync(path, content, CancellationToken.None);

            var error = await Should.ThrowAsync<InvalidOperationException>(() => SupervisorChecks.AssertStoppedAsync(
                source, int.MaxValue, requireMarkers: true, requireDescendant: true, CancellationToken.None));

            error.Message.ShouldContain("required complete PID marker .descendant.pid");
            foreach (var (path, content) in witnesses)
            {
                File.Exists(path).ShouldBeTrue("failed stop assertion must retain " + Path.GetFileName(path));
                (await File.ReadAllTextAsync(path, CancellationToken.None)).ShouldBe(content);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
