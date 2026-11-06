using Romd.Domain.Source.Rom;

namespace Romd.Application.Common.Content;

/// <summary>Whole-container selection. Access must cover every BIN; a per-ROM target cannot construct this lane.</summary>
public sealed record RomCdArchiveDownload(int LayoutId, Guid PublicationId, RomRepresentationFacts Representation, string Filename);

public interface IRomCdArchiveDownloadCatalog
{
    Task<RomCdArchiveDownload?> FindForLayoutAsync(int layoutId, CancellationToken ct = default);
}

public interface IRomCdArchiveDownloadReader
{
    Task<Stream?> OpenAsync(RomCdArchiveDownload target, CancellationToken ct = default);
    Task<Stream?> OpenCueAsync(RomCdArchiveDownload target, CancellationToken ct = default);
}
