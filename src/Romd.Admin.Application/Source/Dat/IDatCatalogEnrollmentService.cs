using ErrorOr;
using Romd.Admin.Application.Ingestion.Jobs;

namespace Romd.Admin.Application.Source.Dat;

public sealed record DatCatalogSubscription(int Id, string CatalogId, string? SystemId, string Name,
    int? PlatformId, int? ActiveDatId, string State, DateTimeOffset? LastCheckedAt, string? Message,
    string? CandidateSha256, Guid? JobId, DateTimeOffset? NextCheckAt = null, bool AutomaticChecksPaused = false);
public sealed record DatCatalogDirectory(bool Enabled, string? Message,
    IReadOnlyList<PublishedDatCatalog> Catalogs, IReadOnlyList<DatCatalogSubscription> Subscriptions);
public interface IDatCatalogEnrollmentService
{
    Task<IReadOnlyList<int>> GetDueCheckIdsAsync(CancellationToken ct);
    Task<ErrorOr<DatCatalogSubscription>> CheckScheduledAsync(int id, CancellationToken ct);
    Task RecordScheduledFailureAsync(int id, CancellationToken ct);
    Task<ErrorOr<Success>> StopAsync(int id, CancellationToken ct);
    Task<ErrorOr<DatCatalogDirectory>> DiscoverAsync(CancellationToken ct);
    Task<ErrorOr<DatCatalogSubscription>> GetAsync(int id, CancellationToken ct);
    Task<ErrorOr<DatCatalogSubscription>> CheckAsync(string catalogId, CancellationToken ct);
    Task<ErrorOr<DatReplacementPreview>> PreviewAsync(int id, CancellationToken ct);
    Task<ErrorOr<DatChangePage>> ChangesAsync(int id, DatChangeQuery query, CancellationToken ct);
    Task<ErrorOr<UploadJobCreationResult>> ApplyAsync(int id, string activeHash, string candidateHash, CancellationToken ct);
}
