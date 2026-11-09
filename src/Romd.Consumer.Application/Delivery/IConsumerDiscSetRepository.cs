using Romd.Application.Common.Content;
using Romd.Consumer.Application.Libraries;
using Romd.Application.Common.Pagination;

namespace Romd.Consumer.Application.Delivery;

public sealed record ConsumerDiscSetPlan(int Id, Guid Revision, string Name, int? ExpectedDiscCount,
    IReadOnlyList<ConsumerDiscSetDisc> Discs);
public sealed record ConsumerDiscSetDisc(int Ordinal, ConsumerReleaseManifest Release, string EntryPoint)
{
    public OriginalDiscLayout? OriginalLayout { get; init; }
    public OriginalDiscLayoutIdentity? LayoutIdentity { get; init; }
    public RomCdArchiveDownload? CdArchive { get; init; }
    public string Readiness { get; init; } = "ready";
}

public interface IConsumerDiscSetRepository
{
    Task<ConsumerLibraryReadResult<ConsumerDiscSetPlan>> ReadStatusAsync(ConsumerLibraryScope scope, int id, CancellationToken ct);
    Task<ConsumerLibraryReadResult<ConsumerDiscSetPlan>> ReadForDownloadAsync(ConsumerLibraryScope scope, int id, CancellationToken ct);
    Task<ConsumerLibraryReadResult<PagedList<ConsumerDiscSetPlan>>> ListPageAsync(
        ConsumerLibraryScope scope, int? titleId, string? cursor, int limit, CancellationToken ct);
}
