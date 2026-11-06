using ErrorOr;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom;

/// <summary>Measured publication under the caller's logical/file locks. Current policy chooses preference and release.</summary>
public sealed class RomRepresentationWriter(IRomRepository roms, IUnitOfWork work, IRomMediaObservationStore observations, IRomMediaCreationStore creations)
{
    public async Task<ErrorOr<RomRetainedMeasurement>> RecordMeasurementAsync(int romId, RomContainerFacts container,
        RomRepresentationVerification measurement, RomLogicalInspection? inspection, CancellationToken ct, FileStoreResult? published = null)
    {
        var retained = await roms.RecordWholeMeasurementStagedAsync(romId, published?.File.Id, container, measurement, ct);
        if (retained is null)
            return RomEligibilityErrors.MeasurementNotRetained();
        if (inspection is not null)
        {
            var observed = await observations.StageAsync(romId, retained, inspection, ct);
            if (observed.IsError)
                return observed.Errors;
        }
        return retained;
    }

    public async Task<ErrorOr<Success>> RecordCreatedEvidenceAsync(int romId, RomRetainedMeasurement retained, PreparedRom.WholeFile prepared, CancellationToken ct)
    {
        if (prepared.SourceEquivalence is not { } comparison)
            return Result.Success;
        return await creations.StageAsync(romId, retained, comparison, prepared.ChdEncodingEvidence, ct);
    }

    public async Task<int> PreferAndReleaseAsync(int romId, RepresentationRetentionDecision decision,
        IReadOnlyList<RetainedRepresentation> retained, RomRetainedMeasurement measured, PreparedRom.WholeFile prepared, CancellationToken ct)
    {
        if (decision.Retention == RepresentationRetention.Discard)
            throw new InvalidOperationException("Discarded evidence cannot change representation ownership.");
        int outputId, fileId;
        if (decision.PreferredArtifact == prepared.Artifact.FileSha256)
        {
            outputId = measured.OutputId;
            fileId = measured.FileId;
        }
        else
        {
            var preferred = retained.Where(row => row.ArtifactSha256 == decision.PreferredArtifact)
                .OrderByDescending(row => row.IsPreferred).ThenBy(row => row.OutputId).First();
            outputId = preferred.OutputId;
            fileId = preferred.FileId;
        }
        if (decision.Release.Any(row => row.OutputId == outputId || !retained.Contains(row)))
            throw new InvalidOperationException("Released outputs must be locked owners other than the selected preference.");
        await work.FlushAsync(ct);
        await roms.SetPreferredRepresentationStagedAsync(romId, outputId, ct);
        await roms.ReleaseRepresentationsStagedAsync(romId, decision.Release.Select(row => row.OutputId).ToArray(), ct);
        return fileId;
    }
}
