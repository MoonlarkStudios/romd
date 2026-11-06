using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Source.Rom;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;

namespace Romd.PostgreSql.TestSupport;

/// <summary>Actual generated identities from a newly published preferred whole-file interpretation.</summary>
public sealed record PublishedWholeMediaFixture(int RomId, RomRepresentationFacts Graph)
{
    public RomRepresentationOutput Output => Graph.Outputs.Single();
    public RomRetainedMeasurement Measurement(Guid verificationId)
    {
        if (!Graph.Verifications.Any(row => row.Id == verificationId))
            throw new ArgumentException("The proof is not in this fixture graph.", nameof(verificationId));
        return new(Graph.Id, Output.Id, Graph.FileId, verificationId);
    }
}

/// <summary>Fresh graph setup inside an explicit caller-owned publication transaction.</summary>
public static class WholeMediaGraphFixtures
{
    public static async Task<PublishedWholeMediaFixture> CreatePreferredMeasuredWholeAsync(this RomdDbContext db, string filename,
        int artifactFileId, RomContainerFacts container, RomRepresentationVerification measurement, Guid actor, DateTimeOffset createdAt, CancellationToken ct)
    {
        RequireTransaction(db);
        if (measurement.Outputs is not [var whole] || whole.Selector is not RomOutputSelector.Whole)
            throw new ArgumentException("A whole-file fixture needs exactly its complete whole measurement.", nameof(measurement));
        var publisher = new MediaFactPublisher(db);
        int romId = await publisher.CreateLogicalAsync(filename, whole.Identity, actor, createdAt, ct);
        var graph = await db.StageMeasuredWholeAsync(artifactFileId, container, measurement, new(RomOutputSelector.Whole.Instance, romId, true), ct);
        return new(romId, graph);
    }

    public static Task<RomRepresentationFacts> StageMeasuredWholeAsync(this RomdDbContext db, int artifactFileId,
        RomContainerFacts container, RomRepresentationVerification measurement, MediaOutputOwner owner, CancellationToken ct)
    {
        RequireTransaction(db);
        return new MediaFactPublisher(db).PublishAsync(artifactFileId, container, RomReconstructionManifest.WholeFile.Instance, measurement, [owner], [], ct);
    }

    public static async Task<PublishedWholeMediaFixture> CreatePreferredUnmeasuredWholeAsync(this RomdDbContext db, string filename,
        int artifactFileId, RomLogicalIdentity identity, RomContainerFacts container, Guid actor, DateTimeOffset createdAt, CancellationToken ct)
    {
        RequireTransaction(db);
        int romId = await new MediaFactPublisher(db).CreateLogicalAsync(filename, identity, actor, createdAt, ct);
        var graph = await db.StageUnmeasuredWholeAsync(artifactFileId, identity, container, romId, true, ct);
        return new(romId, graph);
    }

    public static async Task<RomRepresentationFacts> StageUnmeasuredWholeAsync(this RomdDbContext db, int artifactFileId,
        RomLogicalIdentity identity, RomContainerFacts container, int? ownerRomId, bool preferred, CancellationToken ct)
    {
        RequireTransaction(db);
        if (preferred && ownerRomId is null)
            throw new ArgumentException("A preferred fixture output requires a logical owner.", nameof(ownerRomId));
        if (ownerRomId is { } romId)
            await db.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM romd."Roms" WHERE "Id" = {romId} FOR NO KEY UPDATE""", ct);
        var file = await db.Files.SingleAsync(row => row.Id == artifactFileId, ct);
        // Captured headers are legal held data. Never erase a retained measurement to manufacture this state.
        var representation = new RomRepresentationEntity { FileId = artifactFileId, Encoding = container.Encoding };
        db.RomRepresentations.Add(representation);
        await db.SaveChangesAsync(ct);
        int id = representation.Id;
        if (container is RomContainerFacts.Rvz rvz)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO romd."RvzContainerParameters" VALUES ({id},{(long)rvz.Version},{(long)rvz.DiscType},{(long)rvz.Compression},
                    {rvz.CompressionLevel},{rvz.BlockBytes},{rvz.HasWiiPartitions},{rvz.LogicalBytes});
                """, ct);
        else if (container is RomContainerFacts.Chd chd)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO romd."ChdContainerParameters" VALUES ({id},{(int)chd.Version},{chd.LogicalBytes},{(int)chd.HunkBytes},{(int)chd.UnitBytes},
                    {(long)chd.HunkCount},{(long)chd.Codecs[0]},{(long)chd.Codecs[1]},{(long)chd.Codecs[2]},{(long)chd.Codecs[3]},false,
                    {chd.StoredRawSha1.ToArray()},{chd.StoredOverallSha1.ToArray()},{chd.MetadataSha256.ToArray()});
                """, ct);
            foreach (var (entry, ordinal) in chd.Metadata.Select((entry, ordinal) => (entry, ordinal)))
                await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO romd."ChdMetadataEntries" VALUES ({id},{ordinal},{(long)entry.Tag},{(int)entry.Flags},{entry.Payload.ToArray()})""", ct);
        }
        var output = new RomRepresentationOutputEntity
        {
            RepresentationId = id,
            RomId = ownerRomId,
            ContentSha256 = identity.Sha256,
            ContentSize = identity.Size,
            Sha1 = identity.Sha1,
            Md5 = identity.Md5,
            Crc32 = identity.Crc32
        };
        db.RepresentationOutputs.Add(output);
        await db.SaveChangesAsync(ct);
        if (preferred)
            await db.RomFiles.Where(row => row.Id == ownerRomId).ExecuteUpdateAsync(set => set.SetProperty(row => row.PreferredOutputId, output.Id), ct);
        return new(id, file.Id, file.Sha256, file.Size, container, RomReconstructionManifest.WholeFile.Instance,
            [new(output.Id, ownerRomId, RomOutputSelector.Whole.Instance, identity)], [], []);
    }

    private static void RequireTransaction(RomdDbContext db)
    {
        if (db.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != IsolationLevel.ReadCommitted)
            throw new InvalidOperationException("Fixture publication requires the scenario's explicit ReadCommitted transaction.");
    }
}
