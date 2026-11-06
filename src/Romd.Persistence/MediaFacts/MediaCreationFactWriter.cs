using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;

namespace Romd.Persistence.MediaFacts;

/// <summary>Writes guarded raw observations and complete source/encoder provenance in a caller-owned publication transaction.</summary>
internal sealed class MediaCreationFactWriter(RomdDbContext context)
{
    internal async Task<Guid> StageCdBootAsync(int representationId, Guid verificationId, string inspectorBuild,
        DateTimeOffset observedAt, RomPs1CdBoot boot, CancellationToken ct)
    {
        var rows = await MediaFactLockSet.ReadAsync(context, [representationId], ct);
        if (!rows.TryGetValue(representationId, out var retained) || retained.Manifest is not RomReconstructionManifest.CdTracks ||
            !retained.Verifications.Any(proof => proof.Id == verificationId && proof.Decoder is RomDecoderIdentity.Native))
            throw new InvalidDataException("A raw CD observation requires its exact complete retained native measurement.");
        var existing = retained.CdBootInspections.SingleOrDefault(inspection => inspection.InspectorBuild == inspectorBuild && inspection.Boot.Profile == boot.Profile);
        if (existing is not null)
            return existing.Boot == boot ? existing.Id : throw new InvalidDataException("The immutable CD inspection/build observation disagrees.");
        var observed = new RomCdBootInspection(Guid.NewGuid(), verificationId, inspectorBuild, observedAt, boot);
        _ = new RomRepresentationFacts(retained.Id, retained.FileId, retained.ArtifactSha256, retained.ArtifactSize, retained.Container, retained.Manifest,
            retained.Outputs, retained.Verifications, retained.Descriptors, retained.Creations, [.. retained.CdBootInspections, observed]);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO romd."CdBootInspections" ("Id","RepresentationId","VerificationId","InspectorBuild","Profile","ObservedAtUtcTicks","FirstTrackIndex01",
                "VolumeBlocks","RootBlock","RootBytes","BootPath","BootBlock","BootBytes","EntryPoint","LoadAddress","LoadBytes")
            VALUES ({observed.Id},{retained.Id},{verificationId},{inspectorBuild},{boot.Profile},{observed.ObservedAt.UtcTicks},{boot.FirstTrackIndex01},
                {(long)boot.VolumeBlocks},{(long)boot.RootBlock},{(long)boot.RootBytes},{boot.BootPath},{(long)boot.BootBlock},{(long)boot.BootBytes},
                {(long)boot.EntryPoint},{(long)boot.LoadAddress},{(long)boot.LoadBytes})
            """, ct);
        return observed.Id;
    }

    internal async Task StageCreationAsync(int representationId, RomRepresentationCreation creation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(creation);
        int[] ids = creation.Sources.Select(source => source.RepresentationId).Append(representationId).Distinct().Order().ToArray();
        var rows = await MediaFactLockSet.ReadAsync(context, ids, ct);
        if (!rows.TryGetValue(representationId, out var target))
            throw new InvalidDataException("The created representation is no longer retained.");
        var existing = target.Creations.SingleOrDefault(row => row.Id == creation.Id);
        if (existing is not null)
        {
            if (!Same(existing, creation))
                throw new InvalidDataException("The immutable encoding operation disagrees with its retained provenance.");
            return;
        }
        _ = new RomRepresentationFacts(target.Id, target.FileId, target.ArtifactSha256, target.ArtifactSize, target.Container, target.Manifest, target.Outputs,
            target.Verifications, target.Descriptors, [.. target.Creations, creation], target.CdBootInspections);
        foreach (var source in creation.Sources)
        {
            var output = target.Outputs.Single(row => row.Id == source.TargetOutputId);
            if (!rows.TryGetValue(source.RepresentationId, out var prior) || prior.FileId != source.FileId ||
                prior.ArtifactSha256 != source.ArtifactSha256 || prior.ArtifactSize != source.ArtifactSize ||
                !prior.Verifications.Any(proof => proof.Id == source.VerificationId) ||
                !prior.Outputs.Any(row => row.Id == source.OutputId && row.Identity == output.Identity && row.RomId == output.RomId))
                throw new InvalidDataException("Each created output requires its exact independently measured equivalent source.");
        }
        var native = creation.NativeEncoder;
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO romd."RepresentationCreations" ("Id","RepresentationId","VerificationId","RecipeId","EncoderBuild","ComparedAtUtcTicks",
                "ParametersSha256","EncoderRid","EncoderSourceTag","EncoderSourceSha256","EncoderRecipeVersion")
            VALUES ({creation.Id},{target.Id},{creation.VerificationId},{creation.Recipe.Value},{creation.EncoderBuild},{creation.ComparedAt.UtcTicks},
                {creation.ParametersSha256.ToArray()},CAST({native?.Rid} AS text),CAST({native?.MameTag} AS text),
                CAST({native?.MameSourceSha256.ToArray()} AS bytea),CAST({native?.RecipeVersion} AS integer))
            """, ct);
        foreach (var source in creation.Sources)
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO romd."CreationSources" ("CreationId","TargetOutputId","SourceRepresentationId","SourceOutputId","SourceVerificationId",
                    "SourceFileId","SourceArtifactSha256","SourceArtifactSize")
                VALUES ({creation.Id},{source.TargetOutputId},{source.RepresentationId},{source.OutputId},{source.VerificationId},{source.FileId},
                    {source.ArtifactSha256.ToArray()},{source.ArtifactSize})
                """, ct);
        if (creation.Cd is { } cd)
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO romd."CdEncodingObservations" ("CreationId","RepresentationId","BootInspectionId","SourceRequestSha256","SourceRequestBytes")
                VALUES ({creation.Id},{target.Id},{cd.BootInspectionId},{cd.SourceRequestSha256.ToArray()},{cd.SourceRequestBytes})
                """, ct);
    }

    private static bool Same(RomRepresentationCreation first, RomRepresentationCreation second) =>
        first.VerificationId == second.VerificationId && first.Recipe == second.Recipe && first.EncoderBuild == second.EncoderBuild &&
        first.ComparedAt == second.ComparedAt && first.ParametersSha256 == second.ParametersSha256 && first.NativeEncoder == second.NativeEncoder && first.Cd == second.Cd &&
        first.Sources.OrderBy(source => source.TargetOutputId).SequenceEqual(second.Sources.OrderBy(source => source.TargetOutputId));
}
