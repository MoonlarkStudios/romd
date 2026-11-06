using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>Real staging/runtime/verifier over a trusted synthetic supervisor; this is not native qualification.</summary>
public sealed class RomMediaVerificationWholeTests
{
    [Fact]
    public async Task AcquireAsync_AggregateRegistrationThrows_ReleasesStagingAndProcessingCapacity()
    {
        using var f = new ChdVerifierTests.Fixture();
        var files = Substitute.For<IFileStorageService>();
        var staging = Substitute.For<IAggregateTempFileFactory>();
        staging.ReserveAggregateAsync(Arg.Any<TempFileAggregateRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var aggregate = await f.Files.ReserveAggregateAsync(call.Arg<TempFileAggregateRequest>(), call.Arg<CancellationToken>());
                aggregate.IsError.ShouldBeFalse();
                return (ErrorOr<ITempFileAggregate>)new WrongJobAggregate(aggregate.Value);
            });
        var facts = Facts(Sha256.Parse(new string('a', 64)), 256);
        var selected = new RomMediaVerificationSource(facts.Id, facts.FileId, facts.ArtifactSha256, facts.ArtifactSize,
            new RomDecoderIdentity.Native(ChdBuildEvidence.Decoder(f.Native.ExpectedIdentity)));
        var catalog = Catalog(facts);
        var cd = new RomCdMediaVerification(f.Files, files, Substitute.For<IMediaWorkerSupervisor>(), f.Runtime, TimeProvider.System);
        var failing = new RomMediaVerification(staging, catalog, files, f.Runtime, f.Options, NoInspection, NoRvz, cd);

        var error = await Should.ThrowAsync<InvalidDataException>(() => failing.AcquireAsync(Guid.NewGuid(), selected));
        error.Message.ShouldBe("Aggregate and processing job identities differ.");
        // Both pools have one slot. A subsequent full acquisition proves neither charge leaked.
        var media = new RomMediaVerification(f.Files, catalog, files, f.Runtime, f.Options, NoInspection, NoRvz, cd);
        var recovered = await media.AcquireAsync(Guid.NewGuid(), selected);
        recovered.IsError.ShouldBeFalse();
        await using var lease = recovered.Value;
        files.ReceivedCalls().ShouldBeEmpty();

        static ChdVerifier NoInspection() => throw new InvalidOperationException("Inspection must not start during acquisition.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerifyAsync_JoinedOrUnknownCustody_UsesOnlyInspectionAndKeepsCapacityUntilSafeRelease(bool stopped)
    {
        using var f = new ChdVerifierTests.Fixture();
        var original = await f.SourceAsync();
        var hash = original.FileSha256;
        var size = original.FileSize;
        var supervisor = f.Supervisor(original);
        supervisor.Stopped = stopped;
        await original.DisposeAsync(); // The aggregate gets the one staging file slot.
        using var verifier = f.Verifier(supervisor);
        var files = Substitute.For<IFileStorageService>();
        files.RetrieveByIdAsync(2, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<Stream?>(new MemoryStream(new byte[size])));
        var facts = Facts(hash, size);
        var decoder = new RomDecoderIdentity.Native(ChdBuildEvidence.Decoder(f.Native.ExpectedIdentity));
        var selected = new RomMediaVerificationSource(facts.Id, facts.FileId, hash, size, decoder);
        var media = new RomMediaVerification(f.Files, Catalog(facts), files, f.Runtime, f.Options, () => verifier, NoRvz,
            new(f.Files, files, supervisor, f.Runtime, TimeProvider.System));
        var admitted = await media.AcquireAsync(Guid.NewGuid(), selected);
        admitted.IsError.ShouldBeFalse();
        await using var lease = admitted.Value;
        var result = await lease.VerifyAsync();
        supervisor.Calls.ShouldBe(1); // Every other supervisor operation throws.
        lease.ProcessTreeStopped.ShouldBe(stopped);
        if (stopped)
        {
            result.IsError.ShouldBeFalse();
            result.Value.ShouldBeOfType<RomMediaVerificationMeasurement.Whole>().Representation.Measurement.ArtifactSha256.ShouldBe(hash);
            var complete = result.Value.ShouldBeOfType<RomMediaVerificationMeasurement.Whole>();
            complete.Representation.Measurement.Decoder.ShouldBeOfType<RomDecoderIdentity.Native>().Build.ShouldBe(decoder.Build);
            complete.Inspection.Identity.ShouldBe(facts.Outputs.Single().Identity);
            complete.Inspection.Prefix.ToArray().ShouldBe(new byte[32]);
            complete.Representation.Measurement.Outputs.Single().Identity.ShouldBe(facts.Outputs.Single().Identity);
            result.Value.Artifact.Sha256.ShouldBe(hash);
            (await lease.ReleaseProcessingAsync()).IsError.ShouldBeFalse();
            using var next = await f.Runtime.AcquireAsync();
        }
        else
        {
            result.FirstError.Code.ShouldBe(RomMediaVerificationErrors.Custody.Code);
            (await lease.ReleaseProcessingAsync()).FirstError.Code.ShouldBe(RomMediaVerificationErrors.Custody.Code);
        }
        await files.DidNotReceiveWithAnyArgs().StageRawAsync(default!, default);
    }

    internal static RomRepresentationFacts Facts(Sha256 artifact, long artifactSize)
    {
        var identity = new RomLogicalIdentity(64 * 2048, Sha256.Parse(new string('a', 64)), Sha1.Parse(new string('b', 40)),
            Md5.Parse(new string('c', 32)), Crc32.Parse("12345678"));
        var container = MediaFactFixtures.Chd(identity.Size, 4096, 2048, [0x7a6c6962, 0x6c7a6d61, 0, 0], identity.Sha1,
            Sha1.Parse(new string('d', 40)), [new(0x44564420, 1, new byte[] { 0 })]);
        return new(3, 2, artifact, artifactSize, container, RomReconstructionManifest.WholeFile.Instance,
            [new(4, null, RomOutputSelector.Whole.Instance, identity)], [], []);
    }
    private static IMediaFactReader Catalog(RomRepresentationFacts facts)
    {
        var catalog = Substitute.For<IMediaFactReader>();
        catalog.ReadAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, RomRepresentationFacts> { [facts.Id] = facts });
        return catalog;
    }
    private static RvzVerifier NoRvz() => throw new InvalidOperationException("No RVZ verification is permitted.");

    private sealed class WrongJobAggregate(ITempFileAggregate inner) : ITempFileAggregate, IMediaStagingAggregate
    {
        public Guid JobId => inner.JobId;
        public IWritableTempFile? Output => inner.Output;
        public IWritableTempFile? Verification => inner.Verification;
        public IReadOnlyList<MediaStagingBinding> StagingBindings => ((IMediaStagingAggregate)inner).StagingBindings
            .Select(binding => binding with { JobId = Guid.Empty }).ToArray();
        public Task<ErrorOr<ITempFile>> MaterializeSourceAsync(Stream input, Sha256 hash, long bytes, CancellationToken ct = default) =>
            inner.MaterializeSourceAsync(input, hash, bytes, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
