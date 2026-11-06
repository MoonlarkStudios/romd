using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Persistence;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Repositories;
using Shouldly;

namespace Romd.Infrastructure.Tests.Persistence;

internal sealed record LogicalInspectionFixture(int RomId, RomRepresentationFacts Original,
    RomRepresentationFacts Chd, RomLogicalInspection Observation)
{
    internal static RomLogicalIdentity Identity { get; } = new(131072, MediaFactGraphFixture.Hash('a'),
        MediaFactGraphFixture.Short('c'), Md5.Parse(new string('d', 32)), Crc32.FromUInt32(0));
    internal static RomPs2BootLayoutFacts Boot => new(131072, 64, 16, 24, 200, 25, 123, 28, 896,
        @"cdrom0:\slus_203.12;1", @"cdrom0:\SLUS_203.12;1", "SLUS_203.12", 0x00100000, 2,
        RomPs2BootLayoutFacts.ProfileVersion, "fixture-boot-inspector");
    internal static RomRetainedMeasurement Receipt(RomRepresentationFacts source) =>
        new(source.Id, source.Outputs.Single().Id, source.FileId, source.Verifications.Single().Id);
    internal static RomLogicalInspection Observe(DateTimeOffset time, string inspectorBuild) =>
        new(Guid.NewGuid(), Identity, "raw-structure/v1", inspectorBuild, time, new byte[32], layout: Boot);
    internal static async Task<LogicalInspectionFixture> PublishAsync(RomdDbContext db, DateTimeOffset time, bool retain)
    {
        db.Files.AddRange(new() { Id = 1, Sha256 = Identity.Sha256, Size = Identity.Size, SizeOnDisk = Identity.Size },
            new() { Id = 2, Sha256 = MediaFactGraphFixture.Hash('b'), Size = 4096, SizeOnDisk = 4096 });
        await db.SaveChangesAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var publisher = new MediaFactPublisher(db);
        int romId = await publisher.CreateLogicalAsync("synthetic.iso", Identity, Guid.Empty, time);
        var identityProof = new RomRepresentationVerification(Guid.NewGuid(), Identity.Sha256, Identity.Size,
            new RomDecoderIdentity.Managed("fixture-hasher"), "complete-original/v1", "fixture-verifier", time,
            [new(RomOutputSelector.Whole.Instance, Identity)], RomContainerFacts.Identity.Instance);
        var original = await publisher.PublishAsync(1, RomContainerFacts.Identity.Instance, RomReconstructionManifest.WholeFile.Instance,
            identityProof, [new(RomOutputSelector.Whole.Instance, romId, true)], []);
        var container = new RomContainerFacts.Chd(5, Identity.Size, 16384, 2048, 8, [0, 0, 0, 0], false,
            Identity.Sha1, MediaFactGraphFixture.Short('f'), []);
        var nativeProof = new RomRepresentationVerification(Guid.NewGuid(), MediaFactGraphFixture.Hash('b'), 4096,
            new RomDecoderIdentity.Native(new("linux-arm64", MediaFactGraphFixture.Short('e'), 2)), "complete-native/v1", "fixture-verifier", time,
            [new(RomOutputSelector.Whole.Instance, Identity)], container, container.StoredRawSha1, container.StoredOverallSha1);
        var native = await publisher.PublishAsync(2, container, RomReconstructionManifest.WholeFile.Instance, nativeProof,
            [new(RomOutputSelector.Whole.Instance, romId, false)], []);
        var observation = Observe(time, "fixture-inspector");
        if (retain)
            (await new RomMediaObservationStore(db, new Romd.Persistence.Repositories.Reconciliation.MediaReconciliationRequests(db, TimeProvider.System))
            .StageAsync(romId, Receipt(original), observation)).IsError.ShouldBeFalse();
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        db.ChangeTracker.Clear();
        return new(romId, original, native, observation);
    }
}
