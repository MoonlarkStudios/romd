using ErrorOr;
using Romd.Admin.Application.Ingestion.Jobs;

namespace Romd.Admin.Application.Source.Rom.Compressed;

public sealed record RomCdIngestIntent(string OriginalFilename, bool ArchiveOnly, bool TrackedOnly, UploadItemContext? UploadItem);

/// <summary>Scoped catalog association and publication after the native child has stopped. No unidentified or inferred original file grouping.</summary>
public interface IRomCdIngestor
{
    Task<ErrorOr<RomIngestResult.CdArchive>> IngestAsync(PreparedRom.Cd source, RomCdIngestIntent intent, CancellationToken ct);
}

/// <summary>Current-scope topology/ownership authority and atomic publication of the complete CD upload outcome.</summary>
public interface IRomCdImportPublication
{
    Task<ErrorOr<RomIngestResult.CdArchive>> PublishAsync(RomCdImportPlan plan, RomCdIngestIntent intent, CancellationToken ct);
}
