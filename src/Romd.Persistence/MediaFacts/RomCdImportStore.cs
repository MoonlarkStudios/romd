using System.Data;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Compressed;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Catalog;

namespace Romd.Persistence.MediaFacts;

/// <summary>Database authority and fact ownership only. Retention, tracking intent, notifications and job completion stay in Application.</summary>
public sealed class RomCdImportStore(RomdDbContext db, IMediaFactPublisher facts, ICatalogCdLayoutStore bindings,
    IFileMutationLock fileLocks) : IRomCdImportStore
{
    public async Task<ErrorOr<IRomCdImportScope>> LockAsync(RomCdImportPlan plan, IReadOnlyList<string> platformKeys,
        Guid actorId, DateTimeOffset now, CancellationToken ct)
    {
        RequireTransaction();
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(platformKeys);
        if (!await bindings.LockScopeAsync(plan.Binding.Cue, ct))
            return RomPublicationErrors.RetryRequired();
        await CatalogCdLayoutRead.LockScopeAsync(db, plan.Binding.Cue.CatalogFileId, ct);
        var tracks = plan.Source.Inspection.Tracks.Select(track => new RomCdImportTrack(track.Placement.Number, track.Identity)).ToArray();
        var current = RomCdImportBindings.Select(tracks, await new RomCdImportCatalogReader(db)
            .FindAsync(tracks.Select(track => track.Identity).ToArray(), platformKeys, ct), platformKeys);
        if (current.IsError || current.Value.TitleId != plan.Binding.TitleId || current.Value.PlatformId != plan.Binding.PlatformId ||
            current.Value.Cue != plan.Binding.Cue || !current.Value.Bins.SequenceEqual(plan.Binding.Bins))
            return RomPublicationErrors.RetryRequired();
        var prior = await CatalogCdLayoutRead.ReadRetainedAsync(db, plan.Binding.Cue.CatalogFileId, ct);
        var requested = plan.Binding.Bins.Select(bin => (Path: bin.Requirement.Name, Identity: bin.Track.Identity)).ToList();
        if (plan.OriginalCue is { } original)
            requested.Add((plan.Binding.Cue.Path, original.Verification.Identity));
        var roots = new Dictionary<string, int>(StringComparer.Ordinal);
        bool created = false;
        foreach (var row in requested.OrderBy(row => row.Identity.Sha256.ToString(), StringComparer.Ordinal).ThenBy(row => row.Path, StringComparer.Ordinal))
        {
            int[] inserted = await db.Database.SqlQuery<int>($"""
                INSERT INTO romd."Roms" ("OriginalFilename","ContentSha256","ContentSize","Sha1","Md5","Crc32","CreatedAt","CreatedByUserId")
                VALUES ({row.Path},{row.Identity.Sha256.ToArray()},{row.Identity.Size},{row.Identity.Sha1.ToArray()},{row.Identity.Md5.ToArray()},
                    {row.Identity.Crc32.ToArray()},{now.ToUnixTimeMilliseconds()},{actorId})
                ON CONFLICT ("ContentSha256") DO NOTHING RETURNING "Id" AS "Value"
                """).ToArrayAsync(ct);
            created |= inserted.Length != 0;
            var root = await db.RomFiles.SingleAsync(rom => rom.ContentSha256 == row.Identity.Sha256, ct);
            if (root.ContentSize != row.Identity.Size || root.Sha1 != row.Identity.Sha1 || root.Md5 != row.Identity.Md5 || root.Crc32 != row.Identity.Crc32)
                throw new InvalidDataException("The retained logical SHA-256 has inconsistent complete byte identity.");
            roots.Add(row.Path, root.Id);
        }
        int[] priorRoms = await CatalogCdLayoutRead.RomIdsAsync(db, plan.Binding.Cue.CatalogFileId, ct);
        int[] romIds = roots.Values.Concat(priorRoms).Distinct().Order().ToArray();
        await db.Database.ExecuteSqlRawAsync("""SELECT 1 FROM romd."Roms" WHERE "Id"=ANY(@ids) ORDER BY "Id" FOR NO KEY UPDATE""",
            [new NpgsqlParameter("ids", romIds)], ct);
        int[] graphIds = (await db.RepresentationOutputs.Where(output => output.RomId != null && romIds.Contains(output.RomId.Value))
            .Select(output => output.RepresentationId).Distinct().ToArrayAsync(ct))
            .Concat(prior is null ? [] : new[] { prior.RepresentationId }).Distinct().Order().ToArray();
        foreach (int id in graphIds)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT pg_advisory_xact_lock(9415,{id}); SELECT 1 FROM romd."Representations" WHERE "Id"={id} FOR NO KEY UPDATE;
                """, ct);
        var graphs = new Dictionary<int, RomRepresentationFacts>();
        foreach (int[] batch in graphIds.Chunk(IMediaFactReader.MaximumBatchSize))
            foreach (var row in await new MediaFactReader(db).ReadLockedPublicationAsync(batch, ct))
                graphs.Add(row.Key, row.Value);
        RomRepresentationFacts? archive = prior is null ? null : graphs.GetValueOrDefault(prior.RepresentationId);
        if (prior is not null && archive is null)
            return RomPublicationErrors.RetryRequired();
        if (archive is not null)
        {
            if (archive.Manifest is not RomReconstructionManifest.CdTracks manifest || archive.Outputs.Count != plan.Binding.Bins.Count)
                return RomPublicationErrors.RetryRequired();
            var owned = new List<RomRepresentationOutput>();
            foreach (var output in archive.Outputs)
            {
                var member = manifest.Members[((RomOutputSelector.Member)output.Selector).Ordinal];
                var bin = plan.Binding.Bins.SingleOrDefault(bin => bin.Requirement.Name == member.Path && bin.Track.Identity == output.Identity);
                if (bin is null || output.RomId is { } old && old != roots[bin.Requirement.Name])
                    return RomPublicationErrors.RetryRequired();
                int id = roots[bin.Requirement.Name];
                if (output.RomId is null)
                    await db.RepresentationOutputs.Where(row => row.Id == output.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.RomId, (int?)id), ct);
                owned.Add(new(output.Id, id, output.Selector, output.Identity));
            }
            archive = Copy(archive, owned, archive.Verifications);
            graphs[archive.Id] = archive;
        }
        RomRepresentationFacts? ordinaryCue = null;
        if (plan.OriginalCue is not null)
        {
            int cueRomId = roots[plan.Binding.Cue.Path];
            var incoming = new RomMeasuredRepresentation(plan.OriginalCue.Container, RomReconstructionManifest.WholeFile.Instance, plan.OriginalCue.Measurement);
            ordinaryCue = graphs.Values.Where(graph => graph.Outputs.Any(output => output.RomId == cueRomId) && incoming.MatchesRetained(graph))
                .OrderBy(graph => graph.Id).FirstOrDefault();
        }
        var hashes = graphs.Values.Select(graph => graph.ArtifactSha256).Concat(graphs.Values.SelectMany(graph => graph.Descriptors).Select(row => row.Sha256))
            .Append(plan.Source.Artifact.FileSha256).Append(plan.Cue.Identity.Sha256).Distinct().OrderBy(hash => hash.ToString(), StringComparer.Ordinal);
        foreach (var hash in hashes)
            await fileLocks.AcquireAsync(hash, ct);
        int[] files = graphs.Values.Select(graph => graph.FileId).Concat(graphs.Values.SelectMany(graph => graph.Descriptors).Select(row => row.FileId)).Distinct().Order().ToArray();
        await db.Database.ExecuteSqlRawAsync("""SELECT 1 FROM romd."Files" WHERE "Id"=ANY(@ids) ORDER BY "Id" FOR NO KEY UPDATE""",
            [new NpgsqlParameter("ids", files)], ct);
        // Catalog projection replaces file rows and their bindings. Logical ownership still retains complete graphs.
        // Expose those locked facts; Application chooses whether their physical parameters should be retained.
        var retained = graphs.Values.Where(graph => graph.Manifest is RomReconstructionManifest.CdTracks manifest &&
            graph.Outputs.Count == plan.Binding.Bins.Count && graph.Outputs.All(output => output.Selector is RomOutputSelector.Member selector &&
                plan.Binding.Bins.Any(bin => bin.Requirement.Name == manifest.Members[selector.Ordinal].Path &&
                    bin.Track.Identity == output.Identity && output.RomId == roots[bin.Requirement.Name]))).OrderBy(graph => graph.Id).ToArray();
        return new Scope(this, db.Database.CurrentTransaction!.TransactionId, plan, roots, prior, archive, retained, ordinaryCue, created, now);
    }

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != IsolationLevel.ReadCommitted)
            throw new InvalidOperationException("CD import authority requires its caller's ReadCommitted transaction.");
    }
    private static RomRepresentationFacts Copy(RomRepresentationFacts graph, IReadOnlyList<RomRepresentationOutput> outputs,
        IReadOnlyList<RomRepresentationVerification> verifications) => new(graph.Id, graph.FileId, graph.ArtifactSha256, graph.ArtifactSize,
            graph.Container, graph.Manifest, outputs, verifications, graph.Descriptors, graph.Creations, graph.CdBootInspections);

    private sealed class Scope(RomCdImportStore owner, Guid transactionId, RomCdImportPlan plan, IReadOnlyDictionary<string, int> roots,
        CatalogCdLayoutSnapshot? prior, RomRepresentationFacts? archive, IReadOnlyList<RomRepresentationFacts> retained, RomRepresentationFacts? ordinaryCue, bool createdLogicalOwners, DateTimeOffset now) : IRomCdImportScope
    {
        private int _completed;
        public IReadOnlyDictionary<string, int> BinRomIds { get; } = new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(
            plan.Binding.Bins.ToDictionary(bin => bin.Requirement.Name, bin => roots[bin.Requirement.Name], StringComparer.Ordinal));
        public RomRepresentationFacts? CurrentArchive { get; } = archive;
        public IReadOnlyList<RomRepresentationFacts> RetainedArchives { get; } = Array.AsReadOnly(retained.ToArray());
        public bool HasNewLogicalOwners { get; } = createdLogicalOwners;
        public Task<ErrorOr<RomCdImportRetained>> CompleteAsync(RomRepresentationFacts target, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                throw new InvalidOperationException("CD import scope completion is single-use.");
            return owner.CompleteAsync(transactionId, plan, roots, prior, CurrentArchive, ordinaryCue, target, now, ct);
        }
    }

    private async Task<ErrorOr<RomCdImportRetained>> CompleteAsync(Guid transactionId, RomCdImportPlan plan, IReadOnlyDictionary<string, int> roots,
        CatalogCdLayoutSnapshot? prior, RomRepresentationFacts? previous, RomRepresentationFacts? ordinaryCue,
        RomRepresentationFacts target, DateTimeOffset now, CancellationToken ct)
    {
        RequireTransaction();
        if (db.Database.CurrentTransaction!.TransactionId != transactionId)
            throw new InvalidOperationException("CD scope belongs to a different transaction.");
        if (target.Manifest is not RomReconstructionManifest.CdTracks targetManifest || target.Outputs.Count != plan.Binding.Bins.Count ||
            target.Outputs.Any(output => output.RomId != roots[targetManifest.Members[((RomOutputSelector.Member)output.Selector).Ordinal].Path]))
            throw new InvalidDataException("CD completion requires every actual logical output owned by this exact catalog scope.");
        RomRepresentationFacts? cueGraph = null;
        if (plan.OriginalCue is { } original)
        {
            var descriptor = target.Descriptors.Single(row => row.Ordinal == plan.CueOrdinal);
            int cueRomId = roots[plan.Binding.Cue.Path];
            if (ordinaryCue is not null)
            {
                if (ordinaryCue.FileId != descriptor.FileId)
                    throw new InvalidDataException("Ordinary CUE custody differs from the exact retained descriptor.");
                bool appended = await facts.AppendVerificationAsync(ordinaryCue.Id, original.Measurement, ct);
                cueGraph = appended ? Copy(ordinaryCue, ordinaryCue.Outputs, [.. ordinaryCue.Verifications, original.Measurement]) : ordinaryCue;
            }
            else
                cueGraph = await facts.PublishAsync(descriptor.FileId, original.Container, RomReconstructionManifest.WholeFile.Instance,
                original.Measurement, [new(RomOutputSelector.Whole.Instance, cueRomId, true)], [], ct);
            int preferred = cueGraph.Outputs.Single().Id;
            await db.RomFiles.Where(rom => rom.Id == cueRomId).ExecuteUpdateAsync(set => set.SetProperty(rom => rom.PreferredOutputId, (int?)preferred), ct);
        }
        var observed = plan.Source.Inspection;
        if (observed.Boot is { } boot && target.ArtifactSha256 == observed.ArtifactIdentity.Sha256 && target.ArtifactSize == observed.ArtifactIdentity.Size)
        {
            var proof = target.Verifications.SingleOrDefault(row => row.Decoder.Equals(new RomDecoderIdentity.Native(observed.Decoder)))
                ?? throw new InvalidDataException("A positive CD boot observation requires the retained measurement of its exact decoder build.");
            await new MediaCreationFactWriter(db).StageCdBootAsync(target.Id, proof.Id, observed.VerifierBuild, observed.ObservedAt, boot, ct);
        }
        bool sameBinding = prior is not null && prior.Requirement == plan.Binding.Cue && prior.RepresentationId == target.Id &&
            prior.MemberOrdinal == plan.CueOrdinal && prior.GeneratorProfile == plan.Cue.GeneratorProfile;
        if (!sameBinding)
        {
            if (prior is not null && !await bindings.WithdrawAsync(prior.Id, prior.Revision, ct))
                return RomPublicationErrors.RetryRequired();
            var bound = await bindings.PublishAsync(new(Guid.NewGuid(), plan.Binding.Cue, target.Id, plan.CueOrdinal, plan.Cue.GeneratorProfile, now), ct);
            if (bound.IsError)
                return bound.Errors;
        }
        foreach (var output in target.Outputs.OrderBy(output => output.RomId))
        {
            int romId = output.RomId!.Value;
            int? preferred = await db.RomFiles.Where(rom => rom.Id == romId).Select(rom => rom.PreferredOutputId).SingleAsync(ct);
            int? preferredGraph = preferred is null ? null : await db.RepresentationOutputs.Where(row => row.Id == preferred).Select(row => (int?)row.RepresentationId).SingleAsync(ct);
            if (preferred is null || prior is not null && preferredGraph == prior.RepresentationId)
                await db.RomFiles.Where(rom => rom.Id == romId).ExecuteUpdateAsync(set => set.SetProperty(rom => rom.PreferredOutputId, (int?)output.Id), ct);
        }
        if (previous is not null && previous.Id != target.Id && !await db.CdArchiveOwners.AnyAsync(row => row.RepresentationId == previous.Id, ct) &&
            !await CatalogCdLayoutRead.HasOwnerAsync(db, previous.Id, ct))
            foreach (var output in previous.Outputs.Where(output => output.RomId is not null))
                if (!await db.RomFiles.AnyAsync(rom => rom.Id == output.RomId && rom.PreferredOutputId == output.Id, ct) &&
                    !await db.OriginalDiscLayoutFiles.AnyAsync(row => row.RomId == output.RomId && row.FileId == previous.FileId, ct))
                    await db.RepresentationOutputs.Where(row => row.Id == output.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.RomId, (int?)null), ct);
        int linked = 0;
        var logical = plan.Binding.Bins.Select(bin => (Id: roots[bin.Requirement.Name], Identity: bin.Track.Identity)).ToList();
        if (plan.OriginalCue is not null)
            logical.Add((roots[plan.Binding.Cue.Path], plan.OriginalCue.Verification.Identity));
        foreach (var row in logical)
            linked += await db.DatRoms.Where(rom => rom.Size == row.Identity.Size && rom.Sha1 == row.Identity.Sha1 && rom.Md5 == row.Identity.Md5 &&
                rom.Crc == row.Identity.Crc32 && rom.RomFileId != row.Id).ExecuteUpdateAsync(set => set.SetProperty(rom => rom.RomFileId, (int?)row.Id), ct);
        var retained = (await new MediaFactReader(db).ReadLockedPublicationAsync([target.Id], ct))[target.Id];
        return new RomCdImportRetained(retained, cueGraph, linked);
    }
}
