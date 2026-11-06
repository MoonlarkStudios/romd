using System.Data;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Romd.Admin.Application.Source.Rom;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Queries;

namespace Romd.Persistence.MediaFacts;

/// <summary>Publishes whole-file creation provenance while the source and independently measured output are retained.</summary>
public sealed class RomMediaCreationStore(RomdDbContext context) : IRomMediaCreationStore
{
    public async Task<ErrorOr<Success>> StageAsync(int romId, RomRetainedMeasurement retained, RomSourceEquivalence comparison,
        RomChdEncodingEvidence? nativeEncoding, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(romId);
        ArgumentNullException.ThrowIfNull(retained);
        ArgumentNullException.ThrowIfNull(comparison);
        ct.ThrowIfCancellationRequested();
        if (context.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != IsolationLevel.ReadCommitted)
            throw new InvalidOperationException("Creation publication requires the caller's ReadCommitted transaction.");
        await context.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM romd."Roms" WHERE "Id" = {romId} FOR NO KEY UPDATE""", ct);
        var candidates = await context.RomArtifacts().Where(row => row.RomId == romId &&
            (row.RepresentationId == retained.RepresentationId || row.ArtifactSha256 == comparison.SourceArtifactSha256 &&
                row.ArtifactSize == comparison.SourceArtifactSize)).ToArrayAsync(ct);
        int[] ids = candidates.Select(row => row.RepresentationId).Distinct().Order().ToArray();
        foreach (int id in ids)
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT pg_advisory_xact_lock(9415,{id}); SELECT 1 FROM romd."Representations" WHERE "Id" = {id} FOR NO KEY UPDATE;
                """, ct);
        int[] files = candidates.Select(row => row.FileId).Concat(await context.ReconstructionMembers
            .Where(row => ids.Contains(row.RepresentationId) && row.StoredFileId != null).Select(row => row.StoredFileId!.Value).ToArrayAsync(ct)).Distinct().Order().ToArray();
        await context.Database.ExecuteSqlRawAsync("""SELECT 1 FROM romd."Files" WHERE "Id" = ANY (@ids) ORDER BY "Id" FOR NO KEY UPDATE""",
            [new NpgsqlParameter("ids", files)], ct);
        var graphs = new Dictionary<int, RomRepresentationFacts>();
        foreach (int[] batch in ids.Chunk(IMediaFactReader.MaximumBatchSize))
            foreach (var graph in await new MediaFactReader(context).ReadLockedPublicationAsync(batch, ct))
                graphs.Add(graph.Key, graph.Value);
        if (!graphs.TryGetValue(retained.RepresentationId, out var target) || target.FileId != retained.FileId ||
            target.Manifest is not RomReconstructionManifest.WholeFile || target.Container.Encoding != comparison.OutputEncoding ||
            target.ArtifactSha256 != comparison.OutputArtifactSha256 || target.ArtifactSize != comparison.OutputArtifactSize ||
            !target.Verifications.Any(proof => proof.Id == retained.VerificationId) ||
            target.Outputs.SingleOrDefault(output => output.Id == retained.OutputId && output.RomId == romId) is not { } output ||
            output.Identity != new RomLogicalIdentity(comparison.SourceContentSize, comparison.SourceContentSha256,
                comparison.SourceSha1, comparison.SourceMd5, comparison.SourceCrc32) ||
            (target.Container is RomContainerFacts.Chd) != (nativeEncoding is not null) || nativeEncoding is not null &&
                (!nativeEncoding.MatchesComparison(comparison) || !Matches(nativeEncoding.Parameters, (RomContainerFacts.Chd)target.Container)))
            return Conflict();
        var existing = target.Creations.SingleOrDefault(creation => creation.Id == comparison.Id);
        if (existing is not null)
            return existing.VerificationId == retained.VerificationId && existing.Recipe.Value == comparison.EncoderRecipe &&
                existing.EncoderBuild == comparison.EncoderBuild && existing.ComparedAt == comparison.ComparedAt &&
                existing.NativeEncoder == nativeEncoding?.Encoder && existing.Sources is [var source] && source.TargetOutputId == retained.OutputId &&
                source.ArtifactSha256 == comparison.SourceArtifactSha256 && source.ArtifactSize == comparison.SourceArtifactSize ? Result.Success : Conflict();
        var sourceGraph = graphs.Values.Where(graph => graph.Manifest is RomReconstructionManifest.WholeFile &&
                graph.ArtifactSha256 == comparison.SourceArtifactSha256 && graph.ArtifactSize == comparison.SourceArtifactSize &&
                graph.Outputs.Any(source => source.RomId == romId && source.Identity == output.Identity) &&
                graph.Verifications.Any(proof => proof.Id != retained.VerificationId))
            .OrderBy(graph => graph.Id).FirstOrDefault();
        if (sourceGraph is null)
            return Conflict();
        var sourceOutput = sourceGraph.Outputs.Single();
        var sourceProof = sourceGraph.Verifications.Where(proof => proof.Id != retained.VerificationId)
            .OrderBy(proof => proof.ObservedAt).ThenBy(proof => proof.Id).First();
        var creation = new RomRepresentationCreation(comparison.Id, retained.VerificationId, RecipeId.Parse(comparison.EncoderRecipe),
            comparison.EncoderBuild, comparison.ComparedAt, target.Container.Fingerprint ?? throw new InvalidDataException("Encoded parameters are missing."),
            nativeEncoding?.Encoder, [new(retained.OutputId, sourceGraph.Id, sourceOutput.Id, sourceProof.Id, sourceGraph.FileId, sourceGraph.ArtifactSha256, sourceGraph.ArtifactSize)]);
        _ = new RomRepresentationFacts(target.Id, target.FileId, target.ArtifactSha256, target.ArtifactSize, target.Container, target.Manifest,
            target.Outputs, target.Verifications, target.Descriptors, [.. target.Creations, creation]);
        await new MediaCreationFactWriter(context).StageCreationAsync(target.Id, creation, ct);
        return Result.Success;
    }
    private static Error Conflict() => Error.Conflict("Rom.CreationConflict", "Creation provenance must bind the exact retained source and independently measured output.");
    private static bool Matches(RomChdContainerParameters observed, RomContainerFacts.Chd retained) =>
        observed.Version == retained.Version && observed.LogicalBytes == retained.LogicalBytes && observed.HunkBytes == retained.HunkBytes &&
        observed.UnitBytes == retained.UnitBytes && observed.HunkCount == retained.HunkCount && observed.HasParent == retained.HasParent &&
        new[] { observed.Codec0, observed.Codec1, observed.Codec2, observed.Codec3 }.SequenceEqual(retained.Codecs) &&
        observed.Metadata.Fingerprint == retained.MetadataSha256;
}
