using System.Data;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Queries;

namespace Romd.Persistence.Repositories;

/// <summary>Resolves logical ownership and complete retained graphs in one snapshot, without persisted policy decisions.</summary>
public sealed class RomContentCatalog(RomdDbContext context, DbContextOptions<RomdDbContext> readOptions) : IRomContentCatalog
{
    public Task<RomContentDescriptor?> FindAsync(Sha256 contentSha256, CancellationToken ct = default) =>
        FindOneAsync(rom => rom.ContentSha256 == contentSha256, ct);

    public Task<RomContentDescriptor?> FindByRomIdAsync(int romId, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(romId);
        return FindOneAsync(rom => rom.Id == romId, ct);
    }

    public Task<IReadOnlyDictionary<int, RomContentDescriptor>> FindByRomIdsAsync(IReadOnlyCollection<int> romIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(romIds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(romIds.Count, IRomContentCatalog.MaximumBatchSize);
        foreach (int id in romIds)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        ct.ThrowIfCancellationRequested();
        int[] ids = romIds.Distinct().ToArray();
        if (ids.Length == 0)
            return Task.FromResult<IReadOnlyDictionary<int, RomContentDescriptor>>(new Dictionary<int, RomContentDescriptor>());
        return InSnapshotAsync(db => DescribeAsync(db, db.RomFiles.Where(rom => ids.Contains(rom.Id)), ct), ct);
    }

    /// <summary>Reads the caller's staged facts after taking the complete publication lock set.</summary>
    internal async Task<IReadOnlyDictionary<int, RomContentDescriptor>> ReadLockedPublicationAsync(IReadOnlyCollection<int> romIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(romIds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(romIds.Count, IRomContentCatalog.MaximumBatchSize);
        foreach (int id in romIds)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        ct.ThrowIfCancellationRequested();
        if (context.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != IsolationLevel.ReadCommitted)
            throw new InvalidOperationException("Content publication reads require the caller's ReadCommitted transaction.");
        int[] ids = romIds.Distinct().Order().ToArray();
        if (ids.Length == 0)
            return new Dictionary<int, RomContentDescriptor>();
        await context.Database.ExecuteSqlRawAsync("""
            SELECT 1 FROM romd."Roms" WHERE "Id" = ANY (@ids) ORDER BY "Id" FOR NO KEY UPDATE
            """, [new NpgsqlParameter("ids", ids)], ct);
        return await DescribeAsync(context, context.RomFiles.Where(rom => ids.Contains(rom.Id)), ct, lockedPublication: true);
    }

    private async Task<RomContentDescriptor?> FindOneAsync(Expression<Func<RomFileEntity, bool>> predicate, CancellationToken ct) =>
        (await InSnapshotAsync(db => DescribeAsync(db, db.RomFiles.Where(predicate), ct), ct)).Values.SingleOrDefault();

    private async Task<IReadOnlyDictionary<int, RomContentDescriptor>> InSnapshotAsync(
        Func<RomdDbContext, Task<IReadOnlyDictionary<int, RomContentDescriptor>>> read, CancellationToken ct)
    {
        // A mutation transaction must not lend its ReadCommitted snapshot to this multi-query read.
        // Catalog reads use committed media facts; mutation paths use the locked publication port.
        if (context.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel == IsolationLevel.ReadCommitted)
        {
            await using var independent = new RomdDbContext(readOptions);
            return await OwnedSnapshotAsync(independent, read, ct);
        }
        if (context.Database.CurrentTransaction is not null)
            return await read(context);
        return await OwnedSnapshotAsync(context, read, ct);
    }

    private static async Task<IReadOnlyDictionary<int, RomContentDescriptor>> OwnedSnapshotAsync(RomdDbContext db,
        Func<RomdDbContext, Task<IReadOnlyDictionary<int, RomContentDescriptor>>> read, CancellationToken ct)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        var result = await read(db);
        await snapshot.CommitAsync(ct);
        return result;
    }

    private static async Task<IReadOnlyDictionary<int, RomContentDescriptor>> DescribeAsync(RomdDbContext db,
        IQueryable<RomFileEntity> selected, CancellationToken ct, bool lockedPublication = false)
    {
        var roms = await selected.ToArrayAsync(ct);
        if (roms.Length == 0)
            return new Dictionary<int, RomContentDescriptor>();
        int[] ids = roms.Select(rom => rom.Id).ToArray();
        var artifacts = await db.RomArtifacts().Where(artifact => ids.Contains(artifact.RomId)).ToArrayAsync(ct);
        var facts = new Dictionary<int, RomRepresentationFacts>();
        int[] representationIds = artifacts.Select(artifact => artifact.RepresentationId).Distinct().Order().ToArray();
        if (lockedPublication)
            foreach (var fact in await MediaFactLockSet.ReadAsync(db, representationIds, ct))
                facts.Add(fact.Key, fact.Value);
        else
            foreach (int[] batch in representationIds.Chunk(IMediaFactReader.MaximumBatchSize))
                foreach (var fact in await new MediaFactReader(db).ReadAsync(batch, ct))
                    facts.Add(fact.Key, fact.Value);
        var references = new Dictionary<int, IReadOnlyList<RomDiscMediaReferenceRecord>>();
        foreach (var batch in roms.Chunk(256))
        {
            var reader = new DiscReferenceAssertionReader(db);
            var assertions = lockedPublication ? await reader.ReadLockedPublicationAsync(batch, ct) : await reader.ReadAsync(batch, ct);
            foreach (var assertion in assertions)
                references.Add(assertion.Key, assertion.Value);
        }
        var inspections = new Dictionary<int, IReadOnlyList<RomLogicalInspection>>();
        foreach (int[] batch in ids.Chunk(256))
        {
            var reader = new LogicalInspectionReader(db);
            var observations = lockedPublication ? await reader.ReadLockedPublicationAsync(batch, ct) : await reader.ReadAsync(batch, ct);
            foreach (var observation in observations)
                inspections.Add(observation.Key, observation.Value);
        }
        var byRom = artifacts.ToLookup(artifact => artifact.RomId);
        return roms.ToDictionary(rom => rom.Id, rom => new RomContentDescriptor(rom.Id,
            rom.PreferredOutputId ?? throw new InvalidDataException("A committed logical ROM has no preferred output."),
            new(rom.ContentSize, rom.ContentSha256, rom.Sha1, rom.Md5, rom.Crc32), rom.OriginalFilename,
            byRom[rom.Id].Select(artifact => new RomContentArtifact(facts[artifact.RepresentationId], artifact.OutputId,
                artifact.IsPreferred, artifact.ArtifactMd5, artifact.ArtifactSha1)).ToArray(), inspections.GetValueOrDefault(rom.Id) ?? [], references.GetValueOrDefault(rom.Id) ?? []));
    }
}
