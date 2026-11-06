using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;

namespace Romd.PostgreSql.TestSupport;

/// <summary>Declared original bytes for catalog tests. Encoded media requires explicit measured container facts.</summary>
public sealed class OriginalRomFixture
{
    public int Id { get; set; }
    public required int FileId { get; init; }
    public required string OriginalFilename { get; init; }
    public required Sha1 Sha1 { get; init; }
    public required Md5 Md5 { get; init; }
    public required Crc32 Crc32 { get; init; }
    public Sha256? ContentSha256 { get; init; }
    public long? ContentSize { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public Guid CreatedByUserId { get; init; }
}

/// <summary>Publishes synthetic original-byte graphs through the actual fact publisher in one transaction.</summary>
public static class OriginalRomFixtures
{
    private static readonly ConditionalWeakTable<RomdDbContext, List<OriginalRomFixture>> Pending = new();

    public static void StageOriginalRom(this RomdDbContext db, OriginalRomFixture original) => Pending.GetOrCreateValue(db).Add(original);
    public static void StageOriginalRoms(this RomdDbContext db, params OriginalRomFixture[] originals) => db.StageOriginalRoms((IEnumerable<OriginalRomFixture>)originals);
    public static void StageOriginalRoms(this RomdDbContext db, IEnumerable<OriginalRomFixture> originals) => Pending.GetOrCreateValue(db).AddRange(originals);

    public static async Task PublishOriginalRomsAsync(this RomdDbContext db, CancellationToken ct = default)
    {
        if (!Pending.TryGetValue(db, out var pending) || pending.Count == 0)
            return;
        var staged = pending.ToArray();
        var fixedIds = staged.Where(original => original.Id > 0).Select(original => original.Id).ToHashSet();
        // Persist parent metadata first. The only application FK to logical ROMs is nullable DatRoms.RomFileId;
        // bind those rows after complete publication, within this same transaction.
        var links = db.ChangeTracker.Entries<DatRomEntity>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified && entry.Entity.RomFileId is { } id && fixedIds.Contains(id))
            .Select(entry => (Row: entry.Entity, Id: entry.Entity.RomFileId)).ToArray();
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
        foreach (var link in links)
            link.Row.RomFileId = null;
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            var publisher = new MediaFactPublisher(db);
            foreach (var original in staged)
            {
                var file = await db.Files.SingleAsync(file => file.Id == original.FileId, ct).ConfigureAwait(false);
                if (original.ContentSha256 is { } hash && hash != file.Sha256 || original.ContentSize is { } size && size != file.Size)
                    throw new InvalidDataException("An original-byte fixture must match its physical file identity.");
                var identity = new RomLogicalIdentity(file.Size, file.Sha256, original.Sha1, original.Md5, original.Crc32);
                if (original.Id > 0)
                    db.RomFiles.Add(new RomFileEntity
                    {
                        Id = original.Id,
                        OriginalFilename = original.OriginalFilename,
                        ContentSha256 = identity.Sha256,
                        ContentSize = identity.Size,
                        Sha1 = identity.Sha1,
                        Md5 = identity.Md5,
                        Crc32 = identity.Crc32,
                        CreatedAt = original.CreatedAt,
                        CreatedByUserId = original.CreatedByUserId
                    });
                else
                    original.Id = await publisher.CreateLogicalAsync(original.OriginalFilename, identity, original.CreatedByUserId, original.CreatedAt, ct).ConfigureAwait(false);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                var proof = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), file.Sha256, file.Size, identity, RomContainerFacts.Identity.Instance,
                    new RomDecoderIdentity.Managed("synthetic-original-fixture/v1"), "identity/v1", "synthetic-original-fixture/v1", original.CreatedAt, null, null);
                await db.StageMeasuredWholeAsync(file.Id, RomContainerFacts.Identity.Instance, proof,
                    new(RomOutputSelector.Whole.Instance, original.Id, true), ct).ConfigureAwait(false);
            }
            foreach (var link in links)
                link.Row.RomFileId = link.Id;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            if (transaction is not null)
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            pending.Clear();
        }
        finally
        {
            foreach (var link in links)
                link.Row.RomFileId = link.Id;
        }
    }
}
