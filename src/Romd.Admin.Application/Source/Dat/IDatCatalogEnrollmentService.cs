using ErrorOr;
using Romd.Application.Common.Pagination;

namespace Romd.Admin.Application.Source.Dat;

public sealed record DatCatalogSubscription(int Id, string CatalogId, string? SystemId, string Name,
    int? PlatformId, int? ActiveDatId, string State, DateTimeOffset? LastCheckedAt, string? Message,
    string? CandidateSha256, Guid? JobId, DateTimeOffset? NextCheckAt = null, bool AutomaticChecksPaused = false);
public sealed record DatCatalogDirectory(bool Enabled, string? Message, IReadOnlyList<PublishedDatCatalog> Catalogs);

/// <summary>The enrolled subscription, and whether this request created it rather than replaying an existing one.</summary>
public sealed record DatSubscriptionEnrollment(DatCatalogSubscription Subscription, bool Created);

public interface IDatCatalogEnrollmentService
{
    Task<IReadOnlyList<int>> GetDueCheckIdsAsync(CancellationToken ct);
    Task RecordScheduledFailureAsync(int id, CancellationToken ct);
    Task<DatCatalogDirectory> ListCatalogsAsync(CancellationToken ct);
    Task<IReadOnlyList<DatCatalogSubscription>> ListAsync(CancellationToken ct);
    Task<ErrorOr<DatCatalogSubscription>> GetAsync(int id, CancellationToken ct);

    Task<ErrorOr<DatReplacementPreview>> PreviewAsync(int id, CancellationToken ct);
    Task<ErrorOr<PagedList<DatEntryChange>>> ListChangesAsync(int id, DatChangePageRequest page, CancellationToken ct);
    Task<ErrorOr<DatEntryChangeDetail>> GetChangeAsync(int id, string entryName, DatChangePageRequest page, CancellationToken ct);
}
