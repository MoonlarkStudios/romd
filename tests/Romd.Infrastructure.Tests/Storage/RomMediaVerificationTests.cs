using NSubstitute;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media.Inspection;
using Romd.Media.TestSupport;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class RomMediaVerificationTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("file")]
    [InlineData("hash")]
    [InlineData("size")]
    public async Task AcquireAsync_ChangedCapturedSource_RejectsBeforeStagingOrRetrieval(string mutation)
    {
        using var f = new ChdVerifierTests.Fixture();
        var identity = new RomLogicalIdentity(131072, Sha256.Parse(new string('a', 64)), Sha1.Parse(new string('b', 40)),
            Md5.Parse(new string('c', 32)), Crc32.Parse("12345678"));
        var container = MediaFactFixtures.Chd(identity.Size, 4096, 2048, [0x7a6c6962, 0x6c7a6d61, 0, 0], identity.Sha1,
            Sha1.Parse(new string('d', 40)), [new(0x44564420, 1, new byte[] { 0 })]);
        var facts = new RomRepresentationFacts(3, 2, Sha256.Parse(new string('e', 64)), 256, container,
            RomReconstructionManifest.WholeFile.Instance, [new(4, null, RomOutputSelector.Whole.Instance, identity)], [], []);
        var catalog = Substitute.For<IMediaFactReader>();
        catalog.ReadAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns(
            mutation == "missing" ? new Dictionary<int, RomRepresentationFacts>() : new Dictionary<int, RomRepresentationFacts> { [3] = facts });
        var staging = Substitute.For<IAggregateTempFileFactory>();
        var files = Substitute.For<IFileStorageService>();
        var request = new RomMediaVerificationSource(facts.Id, mutation == "file" ? 7 : facts.FileId,
            mutation == "hash" ? Sha256.Parse(new string('f', 64)) : facts.ArtifactSha256,
            mutation == "size" ? facts.ArtifactSize + 1 : facts.ArtifactSize, new RomDecoderIdentity.Native(ChdBuildEvidence.Decoder(f.Native.ExpectedIdentity)));
        var media = new RomMediaVerification(staging, catalog, files, f.Runtime, f.Options, NoChd, NoRvz,
            new(f.Files, files, Substitute.For<IMediaWorkerSupervisor>(), f.Runtime, TimeProvider.System));

        (await media.AcquireAsync(Guid.NewGuid(), request)).FirstError.Code.ShouldBe(RomMediaVerificationErrors.Mismatch.Code);

        staging.ReceivedCalls().ShouldBeEmpty();
        files.ReceivedCalls().ShouldBeEmpty();
        using var capacity = await f.Runtime.AcquireAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyAsync_RetainedRvz_IndependentlyMeasuresManagedBuildAndRejectsDifferentRequestedBuild(bool wrongDecoder)
    {
        using var child = new MediaWorkerTestFixture();
        using var f = new ChdVerifierTests.Fixture();
        var disc = new SyntheticRvzBuilder(0x100000, 0x20000).Padding(0x18000, 0x8000, new byte[68]).Build();
        using var verifier = new RvzVerifier(f.Options, new MediaWorkerSupervisor(child.Options, () => new MediaWorkerProcessLifetime(), runtime: f.Runtime), f.Runtime);
        var identity = MediaFactFixtures.Identity(disc.ExpectedDisc);
        var container = new RomContainerFacts.Rvz(0x01000000, 1, 5, 3, 131072, false, identity.Size);
        var facts = new RomRepresentationFacts(3, 2, MediaFactFixtures.Identity(disc.File).Sha256, disc.File.Length, container,
            RomReconstructionManifest.WholeFile.Instance, [new(4, null, RomOutputSelector.Whole.Instance, identity)], [], []);
        var catalog = Substitute.For<IMediaFactReader>();
        catalog.ReadAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<int, RomRepresentationFacts> { [facts.Id] = facts });
        var files = Substitute.For<IFileStorageService>();
        files.RetrieveByIdAsync(2, Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(disc.File));
        var decoder = new RomDecoderIdentity.Managed(wrongDecoder ? "different-managed-build" : MediaInspectionService.DecoderBuild);
        var media = new RomMediaVerification(f.Files, catalog, files, f.Runtime, f.Options, NoChd, () => verifier,
            new(f.Files, files, Substitute.For<IMediaWorkerSupervisor>(), f.Runtime, TimeProvider.System));
        var acquired = await media.AcquireAsync(Guid.NewGuid(), new(facts.Id, facts.FileId, facts.ArtifactSha256, facts.ArtifactSize, decoder));
        acquired.IsError.ShouldBeFalse();
        await using var lease = acquired.Value;

        var result = await lease.VerifyAsync();

        lease.ProcessTreeStopped.ShouldBeTrue();
        if (wrongDecoder)
            result.FirstError.Code.ShouldBe(RomMediaVerificationErrors.Mismatch.Code);
        else
        {
            result.IsError.ShouldBeFalse(result.IsError ? result.FirstError.ToString() : null);
            var measured = result.Value.ShouldBeOfType<RomMediaVerificationMeasurement.Whole>();
            measured.Representation.MatchesRetained(facts).ShouldBeTrue();
            measured.Representation.Measurement.Decoder.Equals(decoder).ShouldBeTrue();
            measured.Inspection.Identity.ShouldBe(identity);
            measured.Artifact.Sha256.ShouldBe(facts.ArtifactSha256);
        }
        await Should.ThrowAsync<InvalidOperationException>(() => lease.VerifyAsync());
        (await lease.ReleaseProcessingAsync()).IsError.ShouldBeFalse();
        using var recovered = await f.Runtime.AcquireAsync();
        await files.DidNotReceiveWithAnyArgs().StageRawAsync(default!, default);
    }

    [Theory]
    [InlineData("decoder")]
    [InlineData("output")]
    [InlineData("parameters")]
    public async Task VerifyAsync_RetainedWholeChd_RejectsContradictoryOutputParametersOrRequestedDecoder(string mutation)
    {
        using var f = new ChdVerifierTests.Fixture();
        var original = await f.SourceAsync();
        var supervisor = f.Supervisor(original);
        using var verifier = f.Verifier(supervisor);
        var facts = RomMediaVerificationWholeTests.Facts(original.FileSha256, original.FileSize);
        await original.DisposeAsync();
        var container = (RomContainerFacts.Chd)facts.Container;
        if (mutation == "parameters")
            container = MediaFactFixtures.Chd(container.LogicalBytes, 8192, container.UnitBytes,
            container.Codecs, container.StoredRawSha1, container.StoredOverallSha1, container.Metadata);
        var logical = facts.Outputs.Single().Identity;
        if (mutation == "output")
            logical = new(logical.Size, Sha256.Parse(new string('f', 64)), logical.Sha1, logical.Md5, logical.Crc32);
        facts = new(facts.Id, facts.FileId, facts.ArtifactSha256, facts.ArtifactSize, container, facts.Manifest,
            [new(4, null, RomOutputSelector.Whole.Instance, logical)], [], []);
        var native = ChdBuildEvidence.Decoder(f.Native.ExpectedIdentity);
        var decoder = new RomDecoderIdentity.Native(mutation == "decoder" ? new(native.Rid, native.UpstreamCommit, native.RecipeVersion + 1) : native);
        var catalog = Substitute.For<IMediaFactReader>();
        catalog.ReadAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<int, RomRepresentationFacts> { [facts.Id] = facts });
        var files = Substitute.For<IFileStorageService>();
        files.RetrieveByIdAsync(facts.FileId, Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(new byte[facts.ArtifactSize]));
        var media = new RomMediaVerification(f.Files, catalog, files, f.Runtime, f.Options, () => verifier, NoRvz,
            new(f.Files, files, supervisor, f.Runtime, TimeProvider.System));
        var acquired = await media.AcquireAsync(Guid.NewGuid(), new(facts.Id, facts.FileId, facts.ArtifactSha256, facts.ArtifactSize, decoder));
        acquired.IsError.ShouldBeFalse();
        await using var lease = acquired.Value;

        (await lease.VerifyAsync()).FirstError.Code.ShouldBe(RomMediaVerificationErrors.Mismatch.Code);

        supervisor.Calls.ShouldBe(1);
        lease.ProcessTreeStopped.ShouldBeTrue();
        (await lease.ReleaseProcessingAsync()).IsError.ShouldBeFalse();
        using var recovered = await f.Runtime.AcquireAsync();
    }

    private static ChdVerifier NoChd() => throw new InvalidOperationException("No CHD verification is permitted.");
    private static RvzVerifier NoRvz() => throw new InvalidOperationException("No RVZ verification is permitted.");
}
