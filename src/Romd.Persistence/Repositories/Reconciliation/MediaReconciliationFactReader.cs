using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Catalog;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;

namespace Romd.Persistence.Repositories.Reconciliation;

/// <summary>One current-fact projection shared by committed snapshots and callers holding the full mutation lock set.</summary>
internal sealed partial class MediaReconciliationFactReader
{
    private readonly RomdDbContext _db;
    private readonly DbContextOptions<RomdDbContext> _readOptions;
    private readonly MediaKindCatalog _kinds;
    internal MediaReconciliationFactReader(RomdDbContext db, DbContextOptions<RomdDbContext> readOptions, MediaKindCatalog kinds)
    {
        this._db = db;
        this._readOptions = readOptions;
        this._kinds = kinds;
    }
    internal Task<MediaReconciliationFactRead> ReadSnapshotAsync(MediaReconciliationSubject subject, CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != IsolationLevel.RepeatableRead)
            throw new InvalidOperationException("Reconciliation snapshots require an owned repeatable-read transaction.");
        return ReadAsync(subject, false, ct);
    }
    internal Task<MediaReconciliationFactRead> ReadLockedAsync(MediaReconciliationSubject subject, CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != IsolationLevel.ReadCommitted)
            throw new InvalidOperationException("Locked reconciliation facts require the caller's read-committed transaction and complete lock set.");
        return ReadAsync(subject, true, ct);
    }
    private static MediaReconciliationFactRead Ready(MediaReconciliationFacts facts) => new MediaReconciliationFactRead.Complete(facts);
    private Task<IReadOnlyDictionary<int, RomRepresentationFacts>> ReadGraphsAsync(int[] ids, bool locked, CancellationToken ct) => locked
        ? new MediaFactReader(_db).ReadLockedPublicationAsync(ids, ct) : new MediaFactReader(_db).ReadAsync(ids, ct);
    private async Task<MediaReconciliationFactRead> ReadAsync(MediaReconciliationSubject subject, bool locked, CancellationToken ct)
    {
        if (subject is MediaReconciliationSubject.LogicalRom logical)
        {
            var catalog = new RomContentCatalog(_db, _readOptions);
            var content = locked ? (await catalog.ReadLockedPublicationAsync([logical.Id], ct)).GetValueOrDefault(logical.Id) : await catalog.FindByRomIdAsync(logical.Id, ct);
            if (content is null)
                return Ready(new MediaReconciliationFacts.None("subject-withdrawn"));
            var platforms = await (from rom in _db.DatRoms
                                   join game in _db.DatGames on rom.DatGameId equals game.Id
                                   join dat in _db.DatFiles on game.DatFileId equals dat.Id
                                   join platform in _db.Platforms on dat.PlatformId equals (int?)platform.Id
                                   where rom.RomFileId == logical.Id
                                   select platform.CanonicalKey).Distinct().ToArrayAsync(ct);
            return Ready(new MediaReconciliationFacts.Logical(content, platforms));
        }
        if (subject is MediaReconciliationSubject.Representation representation)
        {
            if (!await MediaRepresentationOwnership.Owned(_db).AnyAsync(row => row.Id == representation.Id, ct))
                return Ready(new MediaReconciliationFacts.None("subject-withdrawn"));
            var graph = (await ReadGraphsAsync([representation.Id], locked, ct)).GetValueOrDefault(representation.Id);
            return Ready(graph is null ? new MediaReconciliationFacts.None("subject-withdrawn") : new MediaReconciliationFacts.Representation(graph));
        }
        return subject switch
        {
            MediaReconciliationSubject.OriginalLayout original => await LayoutAsync(original.Id, locked, ct),
            MediaReconciliationSubject.CatalogCdLayout catalog => await CatalogLayoutAsync(catalog.Id, locked, ct),
            _ => throw new ArgumentException("Unsupported reconciliation subject namespace.", nameof(subject))
        };
    }
    private async Task<MediaReconciliationFactRead> LayoutAsync(int id, bool locked, CancellationToken ct)
    {
        var cdKinds = _kinds.Kinds.Where(kind => kind.ManifestType == MediaManifestType.CdTracks).ToArray();
        if (cdKinds.Length == 0)
            return Ready(new MediaReconciliationFacts.None("kind-unregistered"));
        MediaReconciliationLayout context;
        List<MediaReconciliationInput> inputs;
        RomRepresentationFacts[] archives;
        int? owner;
        bool alignment;
        FileEntityPersistence cueFile;
        var layout = await _db.OriginalDiscLayouts.Include(row => row.Files).SingleOrDefaultAsync(row => row.Id == id, ct);
        if (layout is null)
            return Ready(new MediaReconciliationFacts.None("subject-withdrawn"));
        if (layout.State is not ("pending" or "accepted") || layout.Issue is not null)
            return Ready(new MediaReconciliationFacts.None("layout-ineligible"));
        string[] paths;
        try
        { paths = JsonSerializer.Deserialize<string[]>(layout.DependencyPathsJson) ?? []; }
        catch (JsonException) { return Ready(new MediaReconciliationFacts.None("layout-incomplete")); }
        if (paths.Length is < 1 or > 99 || paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            return Ready(new MediaReconciliationFacts.None("layout-incomplete"));
        Guid revision = layout.State == "accepted" ? layout.Revision : Guid.Empty;
        var members = layout.Files.Where(file => file.BindingRevision == revision && paths.Contains(file.Path, StringComparer.Ordinal)).ToArray();
        if (members.Length != paths.Length)
            return Ready(new MediaReconciliationFacts.None("layout-incomplete"));
        int[] romIds = members.Select(file => file.RomId).Distinct().ToArray();
        var backing = await OriginalDiscLayoutBacking.ReadAsync(_db, romIds, ct);
        var logical = await _db.RomFiles.Where(rom => romIds.Contains(rom.Id)).ToDictionaryAsync(rom => rom.Id, ct);
        if (members.Any(member => !logical.TryGetValue(member.RomId, out var rom) || rom.ContentSize is not > 0 || rom.ContentSize % 2352 != 0 ||
            !backing.Any(row => row.RomId == member.RomId && row.FileId == member.FileId)))
            return Ready(new MediaReconciliationFacts.None("layout-incomplete"));
        var hashes = logical.Values.Select(rom => (Romd.Domain.Hashing.Sha1?)rom.Sha1).ToArray();
        string[] platforms = cdKinds.SelectMany(kind => kind.PlatformKeys).ToArray();
        int[] candidates = await (from file in _db.CatalogReleaseFiles
                                  join release in _db.CatalogReleases on file.CatalogReleaseId equals release.Id
                                  join platform in _db.Platforms on release.PlatformId equals platform.Id
                                  where file.Sha1 != null && hashes.Contains(file.Sha1) && platforms.Contains(platform.CanonicalKey) && !release.HasTitleConflict
                                  select release.Id).Distinct().Take(257).ToArrayAsync(ct);
        if (candidates.Length is < 1 or > 256)
            return Ready(new MediaReconciliationFacts.None("layout-catalog-ineligible"));
        var catalogFiles = await _db.CatalogReleaseFiles.Where(file => candidates.Contains(file.CatalogReleaseId)).OrderBy(file => file.Id).ToArrayAsync(ct);
        candidates = candidates.Where(candidate =>
        {
            var files = catalogFiles.Where(file => file.CatalogReleaseId == candidate).ToArray();
            var bins = files.Where(file => file.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)).ToArray();
            return bins.Length == members.Length && files.Count(file => file.Name.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)) <= 1 &&
                files.All(file => file.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || file.Name.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)) &&
                bins.All(file => file.Sha1 != null && file.Size > 0) &&
                bins.GroupBy(file => (file.Sha1!.Value, file.Size)).OrderBy(group => group.Key.Value.ToString()).Select(group => (group.Key, Count: group.Count()))
                    .SequenceEqual(members.GroupBy(member => (logical[member.RomId].Sha1, logical[member.RomId].ContentSize)).OrderBy(group => group.Key.Sha1.ToString()).Select(group => (group.Key, Count: group.Count())));
        }).ToArray();
        if (candidates is not [var selected])
            return Ready(new MediaReconciliationFacts.None("layout-catalog-ambiguous"));
        var releaseRow = await _db.CatalogReleases.SingleAsync(release => release.Id == selected, ct);
        var platformKey = await _db.Platforms.Where(platform => platform.Id == releaseRow.PlatformId).Select(platform => platform.CanonicalKey).SingleAsync(ct);
        var kind = cdKinds.SingleOrDefault(candidate => candidate.PlatformKeys.Contains(platformKey, StringComparer.Ordinal));
        if (kind is null)
            return Ready(new MediaReconciliationFacts.None("kind-unregistered"));
        if (layout.State == "accepted" && (releaseRow.Id != layout.ReleaseId || releaseRow.Fingerprint != layout.Fingerprint))
            return Ready(new MediaReconciliationFacts.None("layout-catalog-changed"));
        string parent = Path.GetDirectoryName(layout.EntryPoint)?.Replace('\\', '/') ?? "";
        string Relative(string path) => parent.Length == 0 ? path : path.StartsWith(parent + "/", StringComparison.Ordinal) ? path[(parent.Length + 1)..] : "../outside";
        string entry = Relative(layout.EntryPoint);
        if (!SafeMember(entry) || members.Any(member => !SafeMember(Relative(member.Path))))
            return Ready(new MediaReconciliationFacts.None("layout-path-ineligible"));
        context = new MediaReconciliationLayout.Original(id, layout.Revision, releaseRow.PlatformId, releaseRow.Id, releaseRow.Fingerprint, entry);
        var selectedBackings = members.Select(member => backing.Where(row => row.RomId == member.RomId && row.FileId == member.FileId).OrderBy(row => row.RepresentationId).ThenBy(row => row.OutputId).First()).ToArray();
        var facts = new Dictionary<int, RomRepresentationFacts>();
        var archiveOwner = await _db.CdArchiveOwners.SingleOrDefaultAsync(row => row.LayoutId == id, ct);
        owner = archiveOwner?.RepresentationId;
        int[] wholeIds = await _db.RepresentationOutputs.Where(output => output.RomId != null && romIds.Contains(output.RomId.Value) && output.MemberOrdinal == null)
            .Select(output => output.RepresentationId).Distinct().OrderBy(value => value).ToArrayAsync(ct);
        var catalogBindings = await CatalogCdLayoutRead.FindForReleaseAsync(_db, selected, ct);
        int[] graphIds = selectedBackings.Select(row => row.RepresentationId).Concat(wholeIds).Concat(owner is { } current ? [current] : [])
            .Concat(catalogBindings.Select(binding => binding.RepresentationId)).Distinct().ToArray();
        foreach (int[] batch in graphIds.Chunk(IMediaFactReader.MaximumBatchSize))
            foreach (var graph in await ReadGraphsAsync(batch, locked, ct))
                facts.Add(graph.Key, graph.Value);
        inputs = members.Select((member, index) =>
        {
            var source = selectedBackings[index];
            var graph = facts[source.RepresentationId];
            return (MediaReconciliationInput)new MediaReconciliationInput.Logical(MediaReconciliationSourceRole.Bin, Relative(member.Path), graph, source.OutputId,
                graph.Verifications.OrderBy(proof => proof.ObservedAt).ThenBy(proof => proof.Id).First().Id);
        }).ToList();
        // Retained whole originals remain this layout's redundant holdings even when a foreign CD archive
        // currently supplies its BIN bytes. Pin and compare their exact graphs before releasing ownership.
        var logicalSources = selectedBackings.Select(row => row.RepresentationId).ToHashSet();
        inputs.AddRange(wholeIds.Where(graph => !logicalSources.Contains(graph)).Select(graph => new MediaReconciliationInput.Artifact(facts[graph], null, null)));
        archives = owner is { } owned ? [facts[owned]] : [];
        if (archives is [var retained] && retained.Manifest is RomReconstructionManifest.CdTracks held)
        {
            var sidecars = held.Members.Select((member, ordinal) => (Member: member, Ordinal: ordinal))
                .Where(pair => pair.Member is RomCdStoredMember { Role: RomCdStoredRole.Sidecar }).ToArray();
            if (sidecars.Any(pair => !SafeMember(pair.Member.Path)))
                return Ready(new MediaReconciliationFacts.None("layout-path-ineligible"));
            foreach (var pair in sidecars)
            {
                var descriptor = retained.Descriptors.Single(descriptor => descriptor.Ordinal == pair.Ordinal);
                inputs.Add(new MediaReconciliationInput.Stored(MediaReconciliationSourceRole.Sidecar, pair.Member.Path, descriptor.FileId, pair.Member.Identity));
            }
            if (sidecars.Length > 0 && !inputs.OfType<MediaReconciliationInput.Logical>().Any(input => input.Graph.Id == retained.Id))
                inputs.Add(new MediaReconciliationInput.Artifact(retained, null, null));
        }
        // Shared BINs may legitimately prefer or bind another layout's archive. Only this owner's association
        // metadata needs alignment; forcing every layout's shared member to its own archive would oscillate.
        alignment = archiveOwner is not null && (archiveOwner.LayoutRevision != layout.Revision || archiveOwner.PlatformId != context.PlatformId ||
            archiveOwner.ReleaseId != context.ReleaseId || archiveOwner.CatalogFingerprint != context.CatalogFingerprint || archiveOwner.EntryPoint != context.EntryPoint);
        cueFile = await _db.Files.SingleAsync(file => file.Id == layout.DescriptorFileId, ct);
        MediaReconciliationInput.Artifact? adoption = null;
        if (catalogBindings is [var binding] && facts.TryGetValue(binding.RepresentationId, out var candidate) &&
            binding.PlatformId == context.PlatformId && binding.Requirement.ReleaseFingerprint == context.CatalogFingerprint &&
            candidate.Manifest is RomReconstructionManifest.CdTracks && candidate.Outputs.Count == members.Length &&
            candidate.Outputs.All(output => output.RomId is { } id && logical.TryGetValue(id, out var rom) &&
                output.Identity.Size == rom.ContentSize && output.Identity.Sha256 == rom.ContentSha256 && output.Identity.Sha1 == rom.Sha1 &&
                output.Identity.Md5 == rom.Md5 && output.Identity.Crc32 == rom.Crc32))
            adoption = new(candidate, null, new(binding.Id, binding.Revision, binding.PlatformId, binding.Requirement, binding.GeneratorProfile));
        return new MediaReconciliationFactRead.Layout(kind.Kind, context, inputs, archives, owner, alignment, cueFile.Id, cueFile.Sha256, cueFile.Size, adoption);
    }
    private static bool SafeMember(string path) => !string.IsNullOrWhiteSpace(path) && path.Length <= 240 && !path.Any(char.IsControl) &&
        !path.Contains('/') && !path.Contains('\\') && path is not ("." or "..");
    internal static bool Renders(RomRepresentationFacts graph, MediaReconciliationSources source)
    {
        var originals = source.Inputs.Where(input => input is not MediaReconciliationInput.Artifact).ToArray();
        return graph.Manifest is RomReconstructionManifest.CdTracks cd && cd.Members.Count == originals.Length && originals.All(input => input switch
        {
            MediaReconciliationInput.Logical logical => cd.Members.Any(member => member is RomCdBinMember && member.Path == logical.Path &&
                member.Identity.Sha256 == logical.Output.Identity.Sha256 && member.Identity.Size == logical.Output.Identity.Size && member.Identity.Sha1 == logical.Output.Identity.Sha1 &&
                member.Identity.Md5 == logical.Output.Identity.Md5 && member.Identity.Crc32 == logical.Output.Identity.Crc32),
            MediaReconciliationInput.Stored stored => cd.Members.Any(member => member is RomCdStoredMember descriptor &&
                descriptor.Role == (stored.Role == MediaReconciliationSourceRole.Cue ? RomCdStoredRole.Cue : RomCdStoredRole.Sidecar) &&
                member.Path == stored.Path && member.Identity == stored.Identity),
            _ => false
        });
    }
}
