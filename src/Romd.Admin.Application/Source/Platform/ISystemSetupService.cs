using ErrorOr;
using Romd.Domain.ReferenceData;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Admin.Application.Source.Dat;

namespace Romd.Admin.Application.Source.Platform;

public interface ISystemSetupService
{
    Task<Page<ManagedSystem>> ListAsync(ReferencePageRequest request, CancellationToken ct);
    Task<ManagedSystemDetails?> GetDetailsAsync(string systemKey, CancellationToken ct);
    Task<ErrorOr<Success>> SetEnabledAsync(int platformId, bool enabled, CancellationToken ct);
    Task<ErrorOr<SystemDatPreview>> PreviewAsync(Stream document, CancellationToken ct);
}

public sealed record ManagedSystem(int Id, string? SystemId, string Name, string ShortName, string? Manufacturer,
    bool Enabled, string State, string? Message, int CatalogCount, int OwnedTitles, int TrackedTitles = 0,
    int KnownTitles = 0, DateTimeOffset? LastDatImportedAt = null);
public sealed record ManagedSystemDetails(SystemResource System, SystemHardware? Hardware);
public sealed record SystemDatPreview(DatDocumentInspection Document, int? SuggestedPlatformId, IReadOnlyList<int> ExistingDatIds);
