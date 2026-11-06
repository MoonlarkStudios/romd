using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Hashing;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Security;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Rom;

public sealed class RomRepresentationPublisherTests
{
    [Fact]
    public async Task PublishAsync_RetainedArtifactReleasedBeforeRomLock_ReturnsTypedRetryWithoutPublishing()
    {
        var incoming = Sha256.Parse(new string('b', 64));
        var originalHash = Sha256.Parse(new string('a', 64));
        var hashes = new DatHashResult(Sha1.Parse(new string('c', 40)), Md5.Parse(new string('d', 32)), Crc32.FromUInt32(1), 100);
        var original = RomFile.Rehydrate(7, "disc.iso", 1, 100, hashes.Sha1, hashes.Md5, hashes.Crc32, DateTimeOffset.UtcNow, originalHash);
        var prepared = new PreparedRom.WholeFile(new Artifact(incoming), "disc.iso", originalHash, hashes, RomRepresentationEncoding.Rvz,
            new(incoming, 20, RomRepresentationEncoding.Rvz, originalHash, 100, hashes.Sha1, hashes.Md5, hashes.Crc32,
                "inspection/v1", "decoder", "verifier", DateTimeOffset.UtcNow), new RomContainerFacts.Rvz(0x01000000, 1, 5, 12, 131072, false, 100));
        var roms = Substitute.For<IRomRepository>();
        roms.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(original);
        var retained = new RetainedRepresentation(12, 102, 2, incoming, new RomContainerFacts.Rvz(0x01000000, 1, 5, 12, 131072, false, 100), true, true);
        var prior = new RetainedRepresentation(11, 101, 1, originalHash, RomContainerFacts.Identity.Instance, true, true);
        roms.ListRepresentationsAsync(7, Arg.Any<CancellationToken>()).Returns(new[] { retained }, new[] { prior });
        var work = Substitute.For<IUnitOfWork>();
        var transaction = Substitute.For<ITransaction>();
        work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        var files = Substitute.For<IFileStorageService>();
        var locks = Substitute.For<IFileMutationLock>();
        var audit = Substitute.For<IAuditContext>();
        audit.ActorId.Returns(Romd.Domain.Identity.SystemActor.UserId);
        audit.IsSystem.Returns(true);
        var publisher = new RomRepresentationPublisher(files, work, roms, TestRomMediaPolicies.Create(), TestRomEncodings.Create(), locks, null, Substitute.For<IMediaFactPublisher>(), Substitute.For<IRomMediaObservationStore>(),
            Substitute.For<IRomMediaCreationStore>(), audit, TimeProvider.System, Substitute.For<Romd.Admin.Application.Source.Rom.Reconciliation.IMediaReconciliationRequests>(), Substitute.For<IRomPs2CurrentReferenceAuthority>());
        bool applied = false;

        var result = await publisher.PublishAsync(prepared, original, true,
            (_, _, _) => { applied = true; return Task.CompletedTask; }, _ => null, default);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(RomPublicationErrors.RetryRequiredCode);
        applied.ShouldBeFalse();
        await roms.Received(1).LockRepresentationSetAsync(7, Arg.Any<CancellationToken>());
        await transaction.Received(1).RollbackAsync(Arg.Any<CancellationToken>());
        await files.DidNotReceive().StageAsync(Arg.Any<ITempFile>(), Arg.Any<CancellationToken>());
        await files.DidNotReceive().PublishStagedAsync(Arg.Any<IStagedFile>(), Arg.Any<CancellationToken>());
        await locks.DidNotReceive().AcquireAsync(Arg.Any<Sha256>(), Arg.Any<CancellationToken>());
        await work.DidNotReceive().FlushAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PublishAsync_RomDeletedBeforeLock_ReturnsTypedRetryWithoutPublishing(bool canRetain, bool beforePreflight)
    {
        var incoming = Sha256.Parse(new string('b', 64));
        var originalHash = Sha256.Parse(new string('a', 64));
        var hashes = new DatHashResult(Sha1.Parse(new string('c', 40)), Md5.Parse(new string('d', 32)), Crc32.FromUInt32(1), 100);
        var original = RomFile.Rehydrate(7, "disc.iso", 1, 100, hashes.Sha1, hashes.Md5, hashes.Crc32, DateTimeOffset.UtcNow, originalHash);
        var prepared = new PreparedRom.WholeFile(new Artifact(incoming), "disc.iso", originalHash, hashes, RomRepresentationEncoding.Rvz,
            new(incoming, 20, RomRepresentationEncoding.Rvz, originalHash, 100, hashes.Sha1, hashes.Md5, hashes.Crc32,
                "inspection/v1", "decoder", "verifier", DateTimeOffset.UtcNow), new RomContainerFacts.Rvz(0x01000000, 1, 5, 12, 131072, false, 100));
        var roms = Substitute.For<IRomRepository>();
        var retained = new RetainedRepresentation(12, 102, 2, incoming, new RomContainerFacts.Rvz(0x01000000, 1, 5, 12, 131072, false, 100), true, true);
        roms.ListRepresentationsAsync(7, Arg.Any<CancellationToken>()).Returns(beforePreflight ? Array.Empty<RetainedRepresentation>() : new[] { retained }, Array.Empty<RetainedRepresentation>());
        var work = Substitute.For<IUnitOfWork>();
        var transaction = Substitute.For<ITransaction>();
        work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        var files = Substitute.For<IFileStorageService>();
        var locks = Substitute.For<IFileMutationLock>();
        var audit = Substitute.For<IAuditContext>();
        audit.ActorId.Returns(Romd.Domain.Identity.SystemActor.UserId);
        audit.IsSystem.Returns(true);
        var publisher = new RomRepresentationPublisher(files, work, roms, TestRomMediaPolicies.Create(), TestRomEncodings.Create(), locks, null, Substitute.For<IMediaFactPublisher>(), Substitute.For<IRomMediaObservationStore>(),
            Substitute.For<IRomMediaCreationStore>(), audit, TimeProvider.System, Substitute.For<Romd.Admin.Application.Source.Rom.Reconciliation.IMediaReconciliationRequests>(), Substitute.For<IRomPs2CurrentReferenceAuthority>());
        bool applied = false;

        var result = await publisher.PublishAsync(prepared, original, canRetain,
            (_, _, _) => { applied = true; return Task.CompletedTask; }, _ => null, default);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(RomPublicationErrors.RetryRequiredCode);
        applied.ShouldBeFalse();
        if (beforePreflight)
        {
            await roms.DidNotReceive().LockRepresentationSetAsync(7, Arg.Any<CancellationToken>());
            await work.DidNotReceive().BeginTransactionAsync(Arg.Any<CancellationToken>());
        }
        else
        {
            await roms.Received(1).LockRepresentationSetAsync(7, Arg.Any<CancellationToken>());
            await transaction.Received(1).RollbackAsync(Arg.Any<CancellationToken>());
        }
        await files.DidNotReceive().StageAsync(Arg.Any<ITempFile>(), Arg.Any<CancellationToken>());
        await files.DidNotReceive().PublishStagedAsync(Arg.Any<IStagedFile>(), Arg.Any<CancellationToken>());
        await locks.DidNotReceive().AcquireAsync(Arg.Any<Sha256>(), Arg.Any<CancellationToken>());
        await work.DidNotReceive().FlushAsync(Arg.Any<CancellationToken>());
    }

    private sealed class Artifact(Sha256 hash) : ITempFile
    {
        public bool TryBeginChildUse() => true;
        public bool TryEndChildUse() => true;
        public bool Quarantine() => true;
        public string Path => "unused";
        public Sha256 FileSha256 => hash;
        public long FileSize => 20;
        public Stream OpenRead() => throw new InvalidOperationException("Evidence-only preflight must not read artifact bytes.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
