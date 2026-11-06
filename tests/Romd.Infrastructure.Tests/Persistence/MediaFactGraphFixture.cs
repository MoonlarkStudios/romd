using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;

namespace Romd.Infrastructure.Tests.Persistence;

/// <summary>Synthetic complete facts exercise persistence integrity, never media/decoder/runtime qualification.</summary>
internal sealed record MediaFactGraphFixture(int RomId, RomRepresentationFacts Source, RomRepresentationFacts Cd)
{
    internal static Sha256 Hash(char value) => Sha256.Parse(new string(value, 64));
    internal static Sha1 Short(char value) => Sha1.Parse(new string(value, 40));
    internal static RomPs1CdBoot Boot => new(RomPs1CdBoot.CurrentProfile, 0, 64, 24, 864, "SLUS_123.45", 26, 4096, 0x80010000, 0x80010000, 2048);
    internal static async Task<MediaFactGraphFixture> CreateAsync(RomdDbContext db, bool audioFirst = false)
    {
        const long bytes = 240 * 2352;
        db.Files.AddRange(new FileEntityPersistence { Id = 1, Sha256 = Hash('a'), Size = bytes, SizeOnDisk = bytes },
            new FileEntityPersistence { Id = 2, Sha256 = Hash('d'), Size = 512, SizeOnDisk = 512 },
            new FileEntityPersistence { Id = 3, Sha256 = Hash('c'), Size = 10, SizeOnDisk = 10 });
        await db.SaveChangesAsync();
        var identity = new RomLogicalIdentity(bytes, Hash('a'), Short('b'), Md5.Parse(new string('c', 32)), Crc32.FromUInt32(0));
        var member = new RomCdFileIdentity(bytes, identity.Sha256, identity.Sha1, identity.Md5, identity.Crc32);
        var cue = new RomCdFileIdentity(10, Hash('c'), Short('c'), Md5.Parse(new string('c', 32)), Crc32.FromUInt32(0));
        var container = new RomContainerFacts.Chd(5, 240 * 2448, 4 * 2448, 2448, 60, [0, 0, 0, 0], false, Short('e'), Short('f'),
            [new(0x43485432,1,"TRACK:1 TYPE:MODE2_RAW SUBTYPE:NONE FRAMES:120 PREGAP:0 PGTYPE:MODE2_RAW PGSUB:NONE POSTGAP:0"u8),
             new(0x43485432,1,"TRACK:2 TYPE:AUDIO SUBTYPE:NONE FRAMES:120 PREGAP:0 PGTYPE:AUDIO PGSUB:NONE POSTGAP:0"u8)]);
        var manifest = new RomReconstructionManifest.CdTracks(240,
            [new(1, 0, 120, audioFirst ? RomCdTrackKind.Audio : RomCdTrackKind.Mode2Raw), new(2, 120, 120, RomCdTrackKind.Audio)],
            [new RomCdBinMember("disc.bin", member, member, [1, 2]), new RomCdStoredMember("disc.cue", cue, RomCdStoredRole.Cue)]);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var publisher = new MediaFactPublisher(db);
        int romId = await publisher.CreateLogicalAsync("disc.bin", identity, Guid.Empty, DateTimeOffset.UnixEpoch);
        var sourceProof = new RomRepresentationVerification(Guid.NewGuid(), Hash('a'), bytes, new RomDecoderIdentity.Managed("fixture-hasher"),
            "identity/v1", "fixture-verifier", DateTimeOffset.UnixEpoch, [new(RomOutputSelector.Whole.Instance, identity)], RomContainerFacts.Identity.Instance);
        var source = await publisher.PublishAsync(1, RomContainerFacts.Identity.Instance, RomReconstructionManifest.WholeFile.Instance, sourceProof,
            [new(RomOutputSelector.Whole.Instance, romId, true)], []);
        var nativeProof = new RomRepresentationVerification(Guid.NewGuid(), Hash('d'), 512, new RomDecoderIdentity.Native(new("linux-arm64", Short('f'), 2)),
            Romd.Media.PlayStation.ChdCdVerification.CurrentProfile, "fixture-verifier", DateTimeOffset.UnixEpoch, [new(new RomOutputSelector.Member(0), identity)], container, Short('e'), Short('f'));
        var cd = await publisher.PublishAsync(2, container, manifest, nativeProof, [new(new RomOutputSelector.Member(0), romId, false)],
            [new(1, 3, cue.Sha256, cue.Size)]);
        await transaction.CommitAsync();
        return new(romId, source, cd);
    }
    internal RomRepresentationCreation Creation(Guid bootId, Guid? id = null, Guid? proofId = null, long requestBytes = 100)
    {
        var encoder = new RomChdEncoderBuild("linux-arm64", "mame0280", Hash('e'), 1);
        return new(id ?? Guid.NewGuid(), proofId ?? Cd.Verifications.Single().Id, RecipeId.Parse("fixture-recipe/v1"), encoder.BuildLabel,
            DateTimeOffset.UnixEpoch.AddTicks(23), Cd.Container.Fingerprint!.Value, encoder,
            [new(Cd.Outputs.Single().Id, Source.Id, Source.Outputs.Single().Id, Source.Verifications.Single().Id, Source.FileId, Source.ArtifactSha256, Source.ArtifactSize)],
            new(bootId, Hash('e'), requestBytes));
    }
}
