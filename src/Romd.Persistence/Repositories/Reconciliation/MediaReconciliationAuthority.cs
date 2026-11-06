using System.Data;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Persistence.MediaFacts;

namespace Romd.Persistence.Repositories.Reconciliation;

/// <summary>Re-derives policy under topology, logical-owner, complete graph and file locks before capture or publication.</summary>
internal sealed class MediaReconciliationAuthority
{
    private readonly RomdDbContext _db;
    private readonly DbContextOptions<RomdDbContext> _readOptions;
    private readonly MediaKindCatalog _kinds;
    private readonly MediaReconciliationDecision _decision;
    internal MediaReconciliationAuthority(RomdDbContext db, DbContextOptions<RomdDbContext> readOptions, MediaKindCatalog kinds, MediaReconciliationDecision decision)
    {
        this._db = db;
        this._readOptions = readOptions;
        this._kinds = kinds;
        this._decision = decision;
    }
    internal async Task<ErrorOr<MediaReconciliationFacts>> CheckAsync(MediaReconciliationCapture capture, MediaReconciliationDemandState demand,
        IReadOnlyCollection<Sha256> publicationHashes, CancellationToken ct)
    {
        if (capture.Identity.Operation is not (MediaReconciliationOperation.Encode or MediaReconciliationOperation.Release or MediaReconciliationOperation.Adopt))
            throw new ArgumentException("Current-policy authority is for encoding and canonical ownership release.", nameof(capture));
        await LockAsync(demand.Subject, capture.Sources, publicationHashes, ct);
        var read = await new MediaReconciliationFactReader(_db, _readOptions, _kinds).ReadLockedAsync(demand.Subject, ct);
        ErrorOr<MediaReconciliationFacts> facts;
        if (read is MediaReconciliationFactRead.Complete complete)
            facts = complete.Facts;
        else
        {
            var layout = (MediaReconciliationFactRead.Layout)read;
            var cue = capture.Sources.Inputs.OfType<MediaReconciliationInput.Stored>().SingleOrDefault(input => input.Role == MediaReconciliationSourceRole.Cue);
            if (cue is null || cue.FileId != layout.CueFileId)
                return MediaReconciliationErrors.Changed;
            facts = layout.WithCue(cue.Identity);
        }
        if (facts.IsError)
            return facts.Errors;
        var current = _decision.Decide(capture.JobId, demand, facts.Value, ct);
        if (current.IsError)
            return current.Errors;
        var expected = current.Value switch
        {
            MediaReconciliationDirective.Adopt adopt => adopt.Capture,
            MediaReconciliationDirective.Encode encode => encode.Capture,
            MediaReconciliationDirective.Release release => release.Capture,
            _ => null
        };
        return expected is not null && expected.Identity.Fingerprint == capture.Identity.Fingerprint ? facts : MediaReconciliationErrors.Changed;
    }

    private async Task LockAsync(MediaReconciliationSubject subject, MediaReconciliationSources sources, IReadOnlyCollection<Sha256> targetHashes, CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != IsolationLevel.ReadCommitted)
            throw new InvalidOperationException("Reconciliation authority requires the caller's read-committed mutation transaction.");
        await CatalogTopologyFence.AcquireAsync(_db, ct);
        if (subject is MediaReconciliationSubject.OriginalLayout layout)
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM romd.\"OriginalDiscLayouts\" WHERE \"Id\"={layout.Id} FOR NO KEY UPDATE", ct);
        if (subject is MediaReconciliationSubject.CatalogCdLayout catalogScope)
            await CatalogCdLayoutRead.LockScopeAsync(_db, catalogScope.Id, ct);
        foreach (var artifact in sources.Inputs.OfType<MediaReconciliationInput.Artifact>().Where(input => input.CatalogWitness is not null))
        {
            var witness = artifact.CatalogWitness!;
            await CatalogCdLayoutRead.LockScopeAsync(_db, witness.Requirement.CatalogFileId, ct);
            var binding = await CatalogCdLayoutRead.ReadAsync(_db, witness.Requirement.CatalogFileId, ct);
            if (!CatalogCdLayoutRead.Matches(binding, witness) || binding!.RepresentationId != artifact.Graph.Id)
                throw new MediaReconciliationLockScopeChangedException();
        }
        int[] scopedRoms = subject switch
        {
            MediaReconciliationSubject.LogicalRom rom => [rom.Id],
            MediaReconciliationSubject.OriginalLayout selected => await _db.OriginalDiscLayoutFiles.Where(row => row.OriginalDiscLayoutId == selected.Id).Select(row => row.RomId).Distinct().ToArrayAsync(ct),
            MediaReconciliationSubject.CatalogCdLayout catalog => await CatalogCdLayoutRead.RomIdsAsync(_db, catalog.Id, ct),
            _ => throw new ArgumentException("Policy reconciliation requires a logical or closed layout scope.", nameof(subject))
        };
        int[] captured = sources.Inputs.Select(input => input switch
        {
            MediaReconciliationInput.Logical logical => logical.Graph.Id,
            MediaReconciliationInput.Artifact artifact => artifact.Graph.Id,
            _ => 0
        }).Where(id => id != 0).Distinct().ToArray();
        async Task<int[]> ScopedGraphsAsync()
        {
            var ids = await _db.RepresentationOutputs.Where(row => row.RomId != null && scopedRoms.Contains(row.RomId.Value)).Select(row => row.RepresentationId).Distinct().ToListAsync(ct);
            if (subject is MediaReconciliationSubject.OriginalLayout owner)
                ids.AddRange(await _db.CdArchiveOwners.Where(row => row.LayoutId == owner.Id).Select(row => row.RepresentationId).ToArrayAsync(ct));
            if (subject is MediaReconciliationSubject.CatalogCdLayout catalog)
                ids.AddRange(await CatalogCdLayoutRead.RepresentationIdsAsync(_db, catalog.Id, ct));
            if (targetHashes.Count != 0)
                ids.AddRange(await (from graph in _db.RomRepresentations
                                    join file in _db.Files on graph.FileId equals file.Id
                                    where targetHashes.Contains(file.Sha256)
                                    select graph.Id).ToArrayAsync(ct));
            return ids.Concat(captured).Distinct().Order().ToArray();
        }
        int[] graphs = await ScopedGraphsAsync();
        int[] roots = (await _db.RepresentationOutputs.Where(row => graphs.Contains(row.RepresentationId) && row.RomId != null)
            .Select(row => row.RomId!.Value).ToArrayAsync(ct)).Concat(scopedRoms).Distinct().Order().ToArray();
        await _db.Database.ExecuteSqlRawAsync("SELECT 1 FROM romd.\"Roms\" WHERE \"Id\"=ANY (@ids) ORDER BY \"Id\" FOR NO KEY UPDATE", [new NpgsqlParameter("ids", roots)], ct);
        if (!(await ScopedGraphsAsync()).SequenceEqual(graphs))
            throw new MediaReconciliationLockScopeChangedException();
        foreach (int graph in graphs)
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(9415,{graph}); SELECT 1 FROM romd.\"Representations\" WHERE \"Id\"={graph} FOR NO KEY UPDATE", ct);
        int[] actualRoots = await _db.RepresentationOutputs.Where(row => graphs.Contains(row.RepresentationId) && row.RomId != null).Select(row => row.RomId!.Value).Distinct().ToArrayAsync(ct);
        if (actualRoots.Except(roots).Any() || !(await ScopedGraphsAsync()).SequenceEqual(graphs))
            throw new MediaReconciliationLockScopeChangedException();
        int[] physical = await _db.Database.SqlQueryRaw<int>("""
            SELECT "FileId" AS "Value" FROM romd."Representations" WHERE "Id"=ANY (@ids)
            UNION SELECT "StoredFileId" AS "Value" FROM romd."ReconstructionMembers" WHERE "RepresentationId"=ANY (@ids) AND "StoredFileId" IS NOT NULL
            """, new NpgsqlParameter("ids", graphs)).ToArrayAsync(ct);
        var fileIds = physical.Concat(sources.Inputs.OfType<MediaReconciliationInput.Stored>().Select(input => input.FileId)).ToHashSet();
        if (subject is MediaReconciliationSubject.OriginalLayout descriptor)
            foreach (int file in await _db.OriginalDiscLayouts.Where(row => row.Id == descriptor.Id).Select(row => row.DescriptorFileId).ToArrayAsync(ct))
                fileIds.Add(file);
        var hashes = (await _db.Files.Where(file => fileIds.Contains(file.Id)).Select(file => file.Sha256).ToArrayAsync(ct)).Concat(targetHashes).Distinct().OrderBy(hash => hash.ToString(), StringComparer.Ordinal).ToArray();
        var locks = new FileMutationLock(_db);
        foreach (var hash in hashes)
            await locks.AcquireAsync(hash, ct);
        // A target may have been admitted while the sorted graph locks were being acquired.
        // Retry the entire transaction; never acquire a newly discovered lower graph/root here.
        if (!(await ScopedGraphsAsync()).SequenceEqual(graphs))
            throw new MediaReconciliationLockScopeChangedException();
        foreach (int file in await _db.Files.Where(file => hashes.Contains(file.Sha256)).Select(file => file.Id).ToArrayAsync(ct))
            fileIds.Add(file);
        await _db.Database.ExecuteSqlRawAsync("SELECT 1 FROM romd.\"Files\" WHERE \"Id\"=ANY (@ids) ORDER BY \"Id\" FOR NO KEY UPDATE", [new NpgsqlParameter("ids", fileIds.Order().ToArray())], ct);
        if (!(await ScopedGraphsAsync()).SequenceEqual(graphs))
            throw new MediaReconciliationLockScopeChangedException();
    }
}

/// <summary>A complete fresh transaction is required after concurrent lock-scope expansion; encoded payloads remain reusable.</summary>
internal sealed class MediaReconciliationLockScopeChangedException : Exception;
