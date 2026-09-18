using ErrorOr;
using Romd.Admin.Application.Ingestion.Jobs;

namespace Romd.Admin.Application.Source.Dat;

public interface IDatSubscriptionService
{
    Task<ErrorOr<DatSubscriptionStatus>> GetAsync(int datId, CancellationToken ct);
    Task<ErrorOr<DatSubscriptionStatus>> CheckAsync(int datId, CancellationToken ct);
    Task<ErrorOr<DatReplacementPreview>> PreviewAsync(int datId, CancellationToken ct);
    Task<ErrorOr<DatChangePage>> ChangesAsync(int datId, DatChangeQuery query, CancellationToken ct);
    Task<ErrorOr<ReplaceDatJobCreationResult>> ApplyAsync(int datId, string activeHash, string candidateHash, CancellationToken ct);
}

public sealed record DatSubscriptionStatus(bool Available, bool Subscribed, string State,
    DateTimeOffset? LastCheckedAt, string? Message, string? CandidateSha256, Guid? JobId);

public sealed record PublishedDatCatalog(string CatalogId, string SystemId, string Name, string Provider,
    string Health, string? DocumentHash, int EntryCount, int FileCount, DateTimeOffset? LastChangedAt);

public interface ISignedDatCatalogClient
{
    bool Enabled { get; }
    Task<ErrorOr<byte[]>> FetchPlayStationAsync(CancellationToken ct);
    Task<ErrorOr<IReadOnlyList<PublishedDatCatalog>>> DiscoverAsync(CancellationToken ct);
    Task<ErrorOr<byte[]>> FetchAsync(string catalogId, string systemId, string expectedName, CancellationToken ct);
}
