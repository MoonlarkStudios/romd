using ErrorOr;

namespace Romd.Admin.Application.Source.Rom.Compressed;

/// <summary>Associates immutable stopped-child observations outside publication transactions, then delegates the fenced commit.</summary>
public sealed class RomCdIngestor(IRomCdImportCatalogReader catalog, IRomCdCueGenerator cues,
    IRomCdImportPublication publication, RomCdStorageRecipes recipes, IRomPreparationService preparation) : IRomCdIngestor
{
    public async Task<ErrorOr<RomIngestResult.CdArchive>> IngestAsync(PreparedRom.Cd source, RomCdIngestIntent intent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(intent);
        var tracks = source.Inspection.Tracks.Select(track => new RomCdImportTrack(track.Placement.Number, track.Identity)).ToArray();
        var platforms = recipes.PlatformsFor(source.Inspection.Boot);
        var entries = await catalog.FindAsync(tracks.Select(track => track.Identity).ToArray(), platforms, ct);
        var bound = RomCdImportBindings.Select(tracks, entries, platforms);
        if (bound.IsError)
            return bound.Errors;
        var generated = await cues.GenerateAsync(source.Inspection.Tracks, bound.Value.Bins.Select(bin => bin.Requirement.Name).ToArray(), ct);
        if (generated.IsError)
            return generated.Errors;
        await using var cue = generated.Value;
        PreparedRom.WholeFile? original = null;
        try
        {
            if (bound.Value.Cue.Matches(cue.Identity))
            {
                var verified = await preparation.PrepareAsync(cue.Artifact, bound.Value.Cue.Path, ct);
                if (verified.IsError)
                    return verified.Errors;
                if (verified.Value is not PreparedRom.WholeFile whole)
                    throw new InvalidDataException("Generated CUE bytes require ordinary whole-file verification.");
                original = whole;
            }
            return await publication.PublishAsync(new(source, bound.Value, cue, original), intent, ct);
        }
        finally { if (original is not null) await original.DisposeAsync(); }
    }
}
