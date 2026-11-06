using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Content;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Validation;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Models;
using Romd.Domain.Catalog;
using Romd.Domain.Paths;
using Romd.Persistence.Entities;
using Romd.Persistence.Queries;

namespace Romd.Persistence.Catalog;

public sealed class OriginalDiscLayoutService(RomdDbContext db, IFileMutationLock fileLocks, TimeProvider? timeProvider = null) : IOriginalDiscLayoutService, IOriginalDiscDescriptorRepository, IOriginalDiscLayoutReader
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private static string[] Paths(OriginalDiscLayoutEntity row) => JsonSerializer.Deserialize<string[]>(row.DependencyPathsJson)!;
    private IQueryable<OriginalDiscLayoutEntity> Rows => db.OriginalDiscLayouts.Include(x => x.Files);

    public async Task RecordStagedAsync(Guid jobId, string relativePath, string entryPoint, int fileId, OriginalCueDescriptor parsed, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Descriptor publication requires a transaction.");
        if (db.ChangeTracker.Entries<OriginalDiscLayoutEntity>().Any(entry => entry.State == EntityState.Added
                && entry.Entity.SourceJobId == jobId && entry.Entity.SourceRelativePath == relativePath)
            || await db.OriginalDiscLayouts.AnyAsync(x => x.SourceJobId == jobId && x.SourceRelativePath == relativePath, ct))
            return;
        db.OriginalDiscLayouts.Add(new()
        {
            SourceJobId = jobId,
            SourceRelativePath = relativePath,
            EntryPoint = entryPoint,
            DescriptorFileId = fileId,
            DependencyPathsJson = JsonSerializer.Serialize(parsed.Dependencies),
            Issue = parsed.Issue,
            Audit = [new() { Action = "intake", At = _clock.GetUtcNow() }]
        });
    }

    public async Task<IReadOnlyList<OriginalDiscDescriptorSource>> ReadUnattachedMatchedCuesAsync(Guid jobId, CancellationToken ct) =>
        await (from item in db.JobItems
               join original in db.OriginalRomContent() on item.RomFileId equals original.RomId
               where item.JobId == jobId && item.Kind == (int)Romd.Domain.Jobs.JobItemKind.Rom &&
                   (item.Outcome == (int)Romd.Domain.Jobs.JobItemOutcome.Ingested || item.Outcome == (int)Romd.Domain.Jobs.JobItemOutcome.Deduplicated) && item.FileName.ToLower().EndsWith(".cue") &&
                   original.Size > 0 && original.Size <= OriginalCueDescriptor.MaximumBytes &&
                   !db.OriginalDiscLayouts.Any(layout => layout.SourceJobId == jobId && layout.SourceRelativePath == item.FileName)
               orderby item.FileName
               select new OriginalDiscDescriptorSource(item.FileName, original.FileId, original.Sha256, original.Size)).Take(100).ToArrayAsync(ct);

    public async Task RecordMatchedStagedAsync(Guid jobId, OriginalDiscDescriptorSource source, OriginalCueDescriptor parsed, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Matched descriptor publication requires a transaction.");
        await fileLocks.AcquireAsync(source.Sha256, ct);
        if (!await db.OriginalRomContent().AnyAsync(row => row.FileId == source.FileId && row.Sha256 == source.Sha256 && row.Size == source.Size, ct))
            return;
        await RecordStagedAsync(jobId, source.RelativePath, source.RelativePath, source.FileId, parsed, ct);
    }

    public async Task<IReadOnlyList<int>> ReconcileStagedAsync(Guid jobId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Descriptor reconciliation requires the worker's fenced transaction.");
        // Journal paths, never loose filename matching, identify successful uploaded siblings.
        var rows = await db.OriginalDiscLayouts.AsTracking().Include(x => x.Files)
            .Where(x => x.SourceJobId == jobId && x.State == "pending" && x.Issue == null).ToListAsync(ct);
        var allPaths = rows.SelectMany(Paths).Distinct(StringComparer.Ordinal).ToArray();
        var items = await db.JobItems.Where(item => item.JobId == jobId && item.RomFileId != null && allPaths.Contains(item.FileName))
            .Select(item => new { item.FileName, RomId = item.RomFileId!.Value }).ToListAsync(ct);
        int[] romIds = items.Select(x => x.RomId).Distinct().ToArray();
        // A sibling already archived by another disc still binds through its current exact backing.
        var candidates = (from item in items
                          join original in (await OriginalDiscLayoutBacking.ReadAsync(db, romIds, ct)).DistinctBy(x => x.RomId)
            on item.RomId equals original.RomId
                          select new { item.FileName, original.RomId, original.FileId }).ToList();
        int[] candidateFileIds = candidates.Select(x => x.FileId).Distinct().ToArray();
        var retained = await db.Files.Where(x => candidateFileIds.Contains(x.Id)).ToListAsync(ct);
        // Locks live until transaction end: order once across the entire job, never per descriptor.
        foreach (var hash in retained.Select(x => x.Sha256).Distinct().OrderBy(x => x.ToString(), StringComparer.Ordinal))
            await fileLocks.AcquireAsync(hash, ct);
        var stillOriginal = await OriginalDiscLayoutBacking.ReadAsync(db, romIds, ct);
        var changed = new HashSet<int>();
        foreach (var row in rows)
        {
            var paths = Paths(row);
            foreach (var match in candidates.Where(x => paths.Contains(x.FileName, StringComparer.Ordinal)
                && stillOriginal.Any(o => o.FileId == x.FileId && o.RomId == x.RomId)))
                if (!row.Files.Any(x => x.Path == match.FileName && x.BindingRevision == Guid.Empty))
                {
                    db.OriginalDiscLayoutFiles.Add(new() { OriginalDiscLayoutId = row.Id, Path = match.FileName, RomId = match.RomId, FileId = match.FileId });
                    changed.Add(row.Id);
                }
        }
        return changed.Order().ToArray();
    }

    public async Task<ErrorOr<Page<OriginalDiscLayoutDto>>> ListAsync(int? platformId, string? cursor, int limit, CancellationToken ct)
    {
        string fingerprint = KeysetCursor.Fingerprint("admin.original-disc-layouts", platformId, "id:asc");
        var position = KeysetCursor.Decode<IntegerPosition>(cursor, fingerprint, value => value.IsValid);
        if (position.IsError)
            return InvalidCursor();
        int after = position.Value?.Id ?? 0;
        var rows = await Rows.Where(x => (platformId == null || x.PlatformId == null || x.PlatformId == platformId)
            && x.Id > after).OrderBy(x => x.Id).Take(limit + 1).ToListAsync(ct);
        var result = new List<OriginalDiscLayoutDto>();
        foreach (var row in rows.Take(limit))
            result.Add(await ToDtoAsync(row, ct));
        return new Page<OriginalDiscLayoutDto>
        {
            Items = result,
            HasNextPage = rows.Count > limit,
            NextCursor = rows.Count > limit ? KeysetCursor.Encode(new IntegerPosition(rows[limit - 1].Id), fingerprint) : null
        };
    }
    public async Task<ErrorOr<OriginalDiscLayoutDto>> GetAsync(int id, CancellationToken ct)
    {
        var row = await Rows.SingleOrDefaultAsync(x => x.Id == id, ct);
        return row is null ? Error.NotFound("OriginalLayout.Missing", "Original layout not found.") : await ToDtoAsync(row, ct);
    }

    public async Task<ErrorOr<Page<OriginalDiscLayoutCandidateDto>>> CandidatesAsync(int id, int? platformId, string? cursor, int limit, CancellationToken ct)
    {
        var descriptor = await (from layout in db.OriginalDiscLayouts
                                join file in db.Files on layout.DescriptorFileId equals file.Id
                                where layout.Id == id
                                select file).SingleOrDefaultAsync(ct);
        if (descriptor is null)
            return Error.NotFound("OriginalLayout.Missing", "Original layout not found.");
        string fingerprint = KeysetCursor.Fingerprint("admin.original-disc-layouts.candidates", id, platformId, "id:asc");
        var position = KeysetCursor.Decode<IntegerPosition>(cursor, fingerprint, value => value.IsValid);
        if (position.IsError)
            return InvalidCursor();
        int after = position.Value?.Id ?? 0;
        // Explicit platform/keyset selection is bounded. Names aid review; only exact hashes bind.
        var releases = await db.CatalogReleases.Where(x => !x.HasTitleConflict && (platformId == null || x.PlatformId == platformId)
            && x.Id > after).OrderBy(x => x.Id).Take(limit + 1).ToListAsync(ct);
        int[] releaseIds = releases.Select(x => x.Id).ToArray();
        var required = await db.CatalogReleaseFiles.Where(x => releaseIds.Contains(x.CatalogReleaseId)).ToListAsync(ct);
        var hashes = required.Where(x => x.Sha1 != null).Select(x => x.Sha1!.Value).Distinct().ToList();
        int[] romIds = await db.RomFiles.Where(x => hashes.Contains(x.Sha1)).Select(x => x.Id).ToArrayAsync(ct);
        // One CD-aware backing read for the page: archived originals remain exact candidates.
        var originals = (await OriginalDiscLayoutBacking.ReadAsync(db, romIds, ct)).DistinctBy(x => x.RomId).ToArray();
        var result = new List<OriginalDiscLayoutCandidateDto>();
        foreach (var release in releases.Take(limit))
        {
            var requirements = required.Where(x => x.CatalogReleaseId == release.Id).ToList();
            var candidates = (from requirement in requirements
                              join original in originals on requirement.Sha1 equals original.Sha1
                              where original.Size == requirement.Size
                              select new { Id = original.RomId, requirement.Name, original.Sha256, original.Size }).ToList();
            var current = await db.OriginalDiscLayouts.Where(x => x.PlatformId == release.PlatformId && x.Fingerprint == release.Fingerprint && x.State == "accepted")
                .Select(x => new { x.Id, x.Revision }).SingleOrDefaultAsync(ct);
            var platform = await db.Platforms.SingleAsync(x => x.Id == release.PlatformId, ct);
            result.Add(new(IdCoder.Encode(release.Id), release.Name, release.Fingerprint, platform.CanonicalKey, platform.Name,
                candidates.Select(x => new OriginalDiscDependencyCandidateDto(IdCoder.Encode(x.Id), x.Name, x.Sha256.ToString(), (ByteCount)x.Size)).ToArray(),
                requirements.Select(x => new OriginalDiscRequirementDto(x.Name, x.Sha1?.ToString(), (ByteCount)x.Size,
                    candidates.Any(c => c.Name == x.Name && c.Size == x.Size),
                    OriginalDiscLayoutCatalogCoverage.SatisfiedByDescriptor(x, descriptor))).ToArray(),
                current is null ? null : IdCoder.Encode(current.Id), current?.Revision));
        }
        return new Page<OriginalDiscLayoutCandidateDto>
        {
            Items = result,
            HasNextPage = releases.Count > limit,
            NextCursor = releases.Count > limit ? KeysetCursor.Encode(new IntegerPosition(releases[limit - 1].Id), fingerprint) : null
        };
    }

    public async Task<ErrorOr<IReadOnlyList<int>>> DecideStagedAsync(int id, OriginalDiscLayoutDecisionRequest request, RevisionPrecondition precondition, Guid actor, CancellationToken ct)
    {
        if (request.Action is not ("confirm" or "reject" or "detach") || request.Reason?.Length > 1000)
            return Error.Validation("OriginalLayout.Decision", "Choose confirm, reject or detach, with a reason of at most 1000 characters.");
        await CatalogTopologyFence.AcquireAsync(db, ct);
        var row = await db.OriginalDiscLayouts.AsTracking().Include(x => x.Files).Include(x => x.Audit).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null)
            return Error.NotFound("OriginalLayout.Missing", "Original layout not found.");
        if (!precondition.IsSatisfiedBy(row.Revision))
            return Error.Custom(412, "OriginalLayout.PreconditionFailed", "The descriptor changed. Reload before saving.");
        Guid revision = Guid.NewGuid();
        var changed = new HashSet<int> { id };
        if (request.Action == "confirm")
        {
            if (row.Issue != null || request.ReleaseId is null || request.Dependencies is not { Count: > 0 and <= 200 } choices
                || choices.Any(x => x == null) || choices.Select(x => x.Path).Distinct(StringComparer.Ordinal).Count() != choices.Count
                || Paths(row).Any(path => !choices.Any(x => x.Path == path))
                || PortablePathSet.Validate(choices.Select(x => x.Path).Prepend(row.EntryPoint)) != null)
                return Error.Validation("OriginalLayout.Dependencies", "Supply every exact safe CUE dependency and required companion.");
            if (!IdCoder.TryDecode(request.ReleaseId, out int releaseId))
                return Error.Validation("OriginalLayout.Release", "Choose a valid release identity.");
            var release = await db.CatalogReleases.SingleOrDefaultAsync(x => x.Id == releaseId && !x.HasTitleConflict, ct);
            if (release is null)
                return Error.Validation("OriginalLayout.Release", "Choose a current, unambiguous catalog release.");
            var required = await db.CatalogReleaseFiles.Where(x => x.CatalogReleaseId == release.Id).ToListAsync(ct);
            var chosen = new List<(OriginalDiscDependencyChoice Choice, int RomId)>();
            foreach (var choice in choices)
            {
                if (!IdCoder.TryDecode(choice.RomId, out int romId))
                    return Error.Validation("OriginalLayout.Content", "Choose a valid exact original content identity.");
                chosen.Add((choice, romId));
            }
            int[] chosenIds = chosen.Select(x => x.RomId).Distinct().ToArray();
            var preferred = await db.RomContent().Where(x => chosenIds.Contains(x.RomId)).ToDictionaryAsync(x => x.RomId, x => x.FileId, ct);
            // Bind the logical original through its current backing, raw or CD archive; prefer the ROM's preferred artifact.
            var backing = (await OriginalDiscLayoutBacking.ReadAsync(db, chosenIds, ct))
                .OrderBy(x => preferred.GetValueOrDefault(x.RomId) == x.FileId ? 0 : 1).ToArray();
            var bindings = new List<OriginalDiscLayoutFileEntity>();
            foreach (var (choice, romId) in chosen)
            {
                var bytes = backing.FirstOrDefault(x => x.RomId == romId);
                if (bytes is null || !required.Any(x => x.Sha1 == bytes.Sha1 && x.Size == bytes.Size)
                    || !Paths(row).Contains(choice.Path, StringComparer.Ordinal) && !required.Any(x => x.Name == choice.Path && x.Sha1 == bytes.Sha1))
                    return Error.Validation("OriginalLayout.Content", "Every dependency must be exact original content required by this release; extra files require an explicit supported role.");
                bindings.Add(new() { Path = choice.Path, RomId = romId, FileId = bytes.FileId, BindingRevision = revision });
            }
            var ids = bindings.Select(x => x.RomId).ToArray();
            var matched = await db.RomFiles.Where(x => ids.Contains(x.Id)).Select(x => new { x.Sha1, x.ContentSize }).ToListAsync(ct);
            var descriptor = await db.Files.SingleAsync(file => file.Id == row.DescriptorFileId, ct);
            if (required.Count == 0 || required.Any(x => x.Sha1 == null ||
                    !matched.Any(m => m.Sha1 == x.Sha1 && m.ContentSize == x.Size) &&
                    !OriginalDiscLayoutCatalogCoverage.SatisfiedByDescriptor(x, descriptor)))
                return Error.Validation("OriginalLayout.Incomplete", "Every catalog-required original companion must be present.");
            // Dependency references follow the same mutation-lock protocol as every CAS owner.
            int[] retainedIds = bindings.Select(x => x.FileId).Distinct().ToArray();
            var retained = await db.Files.Where(x => retainedIds.Contains(x.Id)).ToListAsync(ct);
            foreach (var hash in retained.Select(x => x.Sha256).Distinct().OrderBy(x => x.ToString(), StringComparer.Ordinal))
                await fileLocks.AcquireAsync(hash, ct);
            var locked = await OriginalDiscLayoutBacking.ReadAsync(db, chosenIds, ct);
            if (await db.Files.CountAsync(x => retainedIds.Contains(x.Id), ct) != retainedIds.Length
                || bindings.Any(binding => !locked.Any(x => x.RomId == binding.RomId && x.FileId == binding.FileId)))
                return Error.Conflict("OriginalLayout.ContentChanged", "Original content changed during repair. Reload before saving.");
            // Validate the replacement fully before changing the previous live layout.
            var previous = await db.OriginalDiscLayouts.AsTracking().Include(x => x.Audit).Where(x => x.Id != id
                && x.PlatformId == release.PlatformId && x.Fingerprint == release.Fingerprint && x.State == "accepted").ToListAsync(ct);
            if (previous.Count > 1 || previous.Count == 1 && (request.ReplacesLayoutId != IdCoder.Encode(previous[0].Id)
                    || request.ReplacesRevision != previous[0].Revision))
                return Error.Conflict("OriginalLayout.ReplacementChanged", "Review the current accepted layout and supply its identity and revision before replacing it.");
            foreach (var old in previous)
            {
                old.State = "detached";
                old.Revision = Guid.NewGuid();
                changed.Add(old.Id);
                old.Audit.Add(new() { Revision = old.Revision, Action = "replaced", Actor = actor, At = _clock.GetUtcNow(), Reason = request.Reason });
            }
            row.PlatformId = release.PlatformId;
            row.ReleaseId = release.Id;
            row.Fingerprint = release.Fingerprint;
            row.State = "accepted";
            foreach (var binding in bindings)
                binding.OriginalDiscLayoutId = row.Id;
            db.OriginalDiscLayoutFiles.AddRange(bindings);
        }
        else
            row.State = request.Action == "reject" ? "rejected" : "detached";
        row.Revision = revision;
        row.Audit.Add(new() { Revision = revision, Action = request.Action, Actor = actor, At = _clock.GetUtcNow(), Reason = request.Reason });
        return ErrorOrFactory.From<IReadOnlyList<int>>(changed.Order().ToArray());
    }

    public Task<OriginalDiscLayoutIdentity?> ReadIdentityAsync(int platformId, int releaseId, string fingerprint, CancellationToken ct) =>
        new ConsumerOriginalDiscLayoutReader(db).ReadIdentityAsync(platformId, releaseId, fingerprint, ct);

    public async Task<bool> HasStaleAsync(int platformId, int releaseId, string fingerprint, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Layout reads require the caller's consistent authorization snapshot.");
        bool bound = await db.OriginalDiscLayouts.AnyAsync(x => x.State == "accepted" && x.PlatformId == platformId
            && (x.ReleaseId == releaseId || x.Fingerprint == fingerprint), ct);
        return bound && await ResolveAsync(platformId, releaseId, fingerprint, ct) is null;
    }

    public async Task<OriginalDiscLayout?> ResolveAsync(int platformId, int releaseId, string fingerprint, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Layout reads require the caller's consistent authorization snapshot.");
        return await new ConsumerOriginalDiscLayoutReader(db).ResolveAsync(platformId, releaseId, fingerprint, ct);
    }

    private static Error InvalidCursor() => FieldErrors.Validation("Request.InvalidParameter", "The cursor is invalid for this query.", "cursor");

    private sealed record AuditPosition(DateTimeOffset At, int Id);

    public async Task<ErrorOr<Page<OriginalDiscLayoutAuditDto>>> AuditAsync(int id, string? cursor, int limit, CancellationToken ct)
    {
        if (!await db.OriginalDiscLayouts.AnyAsync(x => x.Id == id, ct))
            return Error.NotFound("OriginalLayout.Missing", "Original layout not found.");
        string fingerprint = KeysetCursor.Fingerprint("admin.original-disc-layouts.audit", id, "at:desc,id:desc");
        var position = KeysetCursor.Decode<AuditPosition>(cursor, fingerprint, value => value.Id > 0 && value.At != default);
        if (position.IsError)
            return InvalidCursor();
        var rows = db.OriginalDiscLayoutAudit.Where(x => x.OriginalDiscLayoutId == id);
        if (position.Value is { } after)
            rows = rows.Where(x => x.At < after.At || x.At == after.At && x.Id < after.Id);
        var items = await rows.OrderByDescending(x => x.At).ThenByDescending(x => x.Id).Take(limit + 1).ToListAsync(ct);
        return new Page<OriginalDiscLayoutAuditDto>
        {
            Items = items.Take(limit).Select(x => new OriginalDiscLayoutAuditDto(x.Revision, x.Action, x.Actor, x.At, x.Reason)).ToArray(),
            HasNextPage = items.Count > limit,
            NextCursor = items.Count > limit
                ? KeysetCursor.Encode(new AuditPosition(items[limit - 1].At, items[limit - 1].Id), fingerprint) : null
        };
    }

    private async Task<OriginalDiscLayoutDto> ToDtoAsync(OriginalDiscLayoutEntity row, CancellationToken ct)
    {
        var descriptor = await db.Files.SingleAsync(x => x.Id == row.DescriptorFileId, ct);
        var candidates = row.Files.Where(x => x.BindingRevision == (row.State == "accepted" ? row.Revision : Guid.Empty)).ToArray();
        var ids = candidates.Select(x => x.FileId).ToArray();
        var files = await db.Files.Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        bool stale = row.State == "accepted" && !await db.CatalogReleases.AnyAsync(x => x.PlatformId == row.PlatformId && x.Fingerprint == row.Fingerprint && !x.HasTitleConflict, ct);
        int[] boundRomIds = candidates.Select(x => x.RomId).Distinct().ToArray();
        // A logical BIN can have several retained outputs in the same archive. Presentation needs
        // one deterministic identity witness; capture/publication still retain every output selector.
        var boundOriginals = (await OriginalDiscLayoutBacking.ReadAsync(db, boundRomIds, ct))
            .OrderBy(x => x.RepresentationId).ThenBy(x => x.OutputId).DistinctBy(x => (x.RomId, x.FileId)).ToArray();
        bool missingOriginal = row.State == "accepted" && candidates.Any(x => !boundOriginals.Any(o => o.RomId == x.RomId && o.FileId == x.FileId));
        string? issue = stale ? "CatalogContentUnavailable" : missingOriginal ? "BoundOriginalUnavailable" : row.Issue;
        if (row.State == "pending" && issue == null)
            issue = Paths(row).Any(path => !candidates.Any(x => x.Path == path)) ? "MissingDependencies" : "ReviewRequired";
        return new(IdCoder.Encode(row.Id), row.Revision, stale || missingOriginal ? "stale" : row.State, issue,
            row.EntryPoint, descriptor.Sha256.ToString(), (ByteCount)descriptor.Size, row.SourceJobId, row.SourceRelativePath,
            row.PlatformId is { } p ? await db.Platforms.Where(x => x.Id == p).Select(x => x.CanonicalKey).SingleOrDefaultAsync(ct) : null, row.ReleaseId is { } r ? IdCoder.Encode(r) : null, row.Fingerprint,
            Paths(row).Select(path =>
            {
                var candidate = candidates.SingleOrDefault(x => x.Path == path);
                var file = candidate == null ? null : boundOriginals.SingleOrDefault(x => x.RomId == candidate.RomId && x.FileId == candidate.FileId);
                return new OriginalDiscDependencyDto(path, candidate is null ? null : IdCoder.Encode(candidate.RomId), file?.Sha256.ToString(), file is null ? null : (ByteCount)file.Size);
            }).ToArray(),
            (await AuditAsync(row.Id, null, 50, ct)).Value);
    }
}
