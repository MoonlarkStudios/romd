using System.Text.Json;
using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media.Inspection;
using Romd.Media.TestSupport;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class RomOriginalMediaInspectionTests
{
    private static MediaReconciliationInput.Logical Source(byte[] bytes)
    {
        var identity = MediaFactFixtures.Identity(bytes);
        var proof = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), identity.Sha256, identity.Size, identity, RomContainerFacts.Identity.Instance,
            new RomDecoderIdentity.Managed("retained-hasher"), "identity/v1", "fixture", DateTimeOffset.UnixEpoch, null, null);
        var graph = new RomRepresentationFacts(3, 2, identity.Sha256, identity.Size, RomContainerFacts.Identity.Instance, RomReconstructionManifest.WholeFile.Instance,
            [new(4, 1, RomOutputSelector.Whole.Instance, identity)], [proof], []);
        return new(MediaReconciliationSourceRole.Whole, "disc.iso", graph, 4, proof.Id);
    }
    private static IMediaFactReader Catalog(MediaReconciliationInput.Logical source)
    {
        var catalog = Substitute.For<IMediaFactReader>();
        catalog.ReadAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<int, RomRepresentationFacts> { [source.Graph.Id] = source.Graph });
        return catalog;
    }
    private static IFileStorageService Files(byte[] bytes)
    {
        var files = Substitute.For<IFileStorageService>();
        files.RetrieveByIdAsync(2, Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(bytes));
        return files;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inspect_RealManagedChild_RequiresExactRequestedBuildAndFullOriginalIdentity(bool wrongDecoder)
    {
        using var child = new MediaWorkerTestFixture();
        using var f = new ChdVerifierTests.Fixture();
        byte[] bytes = SyntheticNintendoIso.GameCube();
        var source = Source(bytes);
        var files = Files(bytes);
        using var verifier = new RvzVerifier(f.Options, new MediaWorkerSupervisor(child.Options, () => new MediaWorkerProcessLifetime(), runtime: f.Runtime), f.Runtime);
        var media = new RomOriginalMediaInspection(f.Files, Catalog(source), files, f.Runtime, f.Options, new RomOriginalMediaIdentifier(verifier));
        var acquired = await media.AcquireAsync(Guid.NewGuid(), source, new(wrongDecoder ? "different-build" : MediaInspectionService.DecoderBuild));
        acquired.IsError.ShouldBeFalse();
        await using var lease = acquired.Value;
        var result = await lease.InspectAsync();
        lease.ProcessTreeStopped.ShouldBeTrue();
        if (wrongDecoder)
            result.FirstError.ShouldBe(RomMediaVerificationErrors.Mismatch);
        else
        {
            result.IsError.ShouldBeFalse();
            result.Value.Representation.MatchesRetained(source.Graph).ShouldBeTrue();
            result.Value.Inspection.Identity.ShouldBe(source.Output.Identity);
            result.Value.Representation.Measurement.Decoder.ShouldBe(new RomDecoderIdentity.Managed(MediaInspectionService.DecoderBuild));
            result.Value.Artifact.Md5.ShouldBe(source.Output.Identity.Md5);
            result.Value.Artifact.Sha1.ShouldBe(source.Output.Identity.Sha1);
        }
        await Should.ThrowAsync<InvalidOperationException>(() => lease.InspectAsync());
        (await lease.ReleaseProcessingAsync()).IsError.ShouldBeFalse();
        using var available = await f.Runtime.AcquireAsync();
        await files.DidNotReceiveWithAnyArgs().StageRawAsync(default!, default);
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("uncertified-cancel")]
    [InlineData("containment")]
    public async Task Inspect_UnknownStop_RetainsProcessingPayloadAndRecoverableExactRunMarker(string failure)
    {
        using var f = new ChdVerifierTests.Fixture();
        byte[] bytes = new byte[256];
        var source = Source(bytes);
        var originals = Substitute.For<IRomOriginalMediaIdentifier>();
        using var cancellation = new CancellationTokenSource();
        MediaProcessingRuntime.MediaProcessingReservation? run = null;
        string? staged = null;
        originals.IdentifyAsync(Arg.Any<ITempFile>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            run = MediaProcessingRuntime.Current;
            staged = call.Arg<ITempFile>().Path;
            if (failure == "uncertified-cancel")
            { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
            if (failure == "throw")
                throw new IOException("Injected missing stop observation.");
            return Task.FromResult<ErrorOr<RomOriginalMediaIdentification>>(Error.Validation("Rom.Media.Containment", "stop not established"));
        });
        Guid job = Guid.NewGuid();
        var media = new RomOriginalMediaInspection(f.Files, Catalog(source), Files(bytes), f.Runtime, f.Options, originals);
        var acquired = await media.AcquireAsync(job, source, new("decoder"));
        acquired.IsError.ShouldBeFalse();
        await using var lease = acquired.Value;
        if (failure == "uncertified-cancel")
            await Should.ThrowAsync<OperationCanceledException>(() => lease.InspectAsync(cancellation.Token));
        else
            (await lease.InspectAsync()).FirstError.ShouldBe(RomMediaVerificationErrors.Custody);
        lease.ProcessTreeStopped.ShouldBeFalse();
        (await lease.ReleaseProcessingAsync()).FirstError.ShouldBe(RomMediaVerificationErrors.Custody);
        run.ShouldNotBeNull();
        staged.ShouldNotBeNull();
        File.Exists(staged).ShouldBeTrue();
        string markerPath = staged + ".quarantine";
        File.Exists(markerPath).ShouldBeTrue();
        using (var marker = JsonDocument.Parse(await File.ReadAllBytesAsync(markerPath)))
        {
            marker.RootElement.EnumerateObject().Count().ShouldBe(3);
            marker.RootElement.GetProperty("Version").GetString().ShouldBe("romd-media-hold/v1");
            marker.RootElement.GetProperty("RunId").GetString().ShouldBe(run.Nonce);
            marker.RootElement.GetProperty("RecordPath").GetString().ShouldBe(run.RecordPath);
        }
        await lease.DisposeAsync();
        File.Exists(staged).ShouldBeTrue();
        await Should.ThrowAsync<ResourceCapacityException>(async () => { using var unavailable = await f.Runtime.AcquireAsync(); });
    }

    [Fact]
    public async Task Inspect_CertifiedStoppedCancellation_PreservesExceptionAndReleasesCapacity()
    {
        using var f = new ChdVerifierTests.Fixture();
        byte[] bytes = new byte[256];
        var source = Source(bytes);
        using var cancellation = new CancellationTokenSource();
        var stopped = new MediaWorkerStoppedCancellationException(cancellation.Token);
        var originals = Substitute.For<IRomOriginalMediaIdentifier>();
        originals.IdentifyAsync(Arg.Any<ITempFile>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromException<ErrorOr<RomOriginalMediaIdentification>>(stopped));
        var media = new RomOriginalMediaInspection(f.Files, Catalog(source), Files(bytes), f.Runtime, f.Options, originals);
        var acquired = await media.AcquireAsync(Guid.NewGuid(), source, new("decoder"));
        acquired.IsError.ShouldBeFalse();
        await using var lease = acquired.Value;
        OperationCanceledException? caught = null;
        try
        { await lease.InspectAsync(cancellation.Token); }
        catch (OperationCanceledException error) { caught = error; }
        caught.ShouldBeSameAs(stopped);
        lease.ProcessTreeStopped.ShouldBeTrue();
        (await lease.ReleaseProcessingAsync()).IsError.ShouldBeFalse();
        using var available = await f.Runtime.AcquireAsync();
    }

    [Fact]
    public async Task Inspect_ChangedPhysicalSource_RefusesBeforeCallingTheInspector()
    {
        using var f = new ChdVerifierTests.Fixture();
        var source = Source(new byte[256]);
        var originals = Substitute.For<IRomOriginalMediaIdentifier>();
        var media = new RomOriginalMediaInspection(f.Files, Catalog(source), Files(Enumerable.Repeat((byte)1, 256).ToArray()), f.Runtime, f.Options, originals);
        var acquired = await media.AcquireAsync(Guid.NewGuid(), source, new("decoder"));
        acquired.IsError.ShouldBeFalse();
        await using var lease = acquired.Value;
        (await lease.InspectAsync()).IsError.ShouldBeTrue();
        originals.ReceivedCalls().ShouldBeEmpty();
        lease.ProcessTreeStopped.ShouldBeTrue();
        (await lease.ReleaseProcessingAsync()).IsError.ShouldBeFalse();
    }
}
