using ErrorOr;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Dat;

namespace Romd.Admin.Application.Source.Platform;

public interface ISystemSetupService
{
    Task<IReadOnlyList<ManagedSystem>> ListAsync(CancellationToken ct);
    Task<ErrorOr<Success>> SetEnabledAsync(int platformId, bool enabled, CancellationToken ct);
    Task<ErrorOr<SystemDatPreview>> PreviewAsync(Stream document, CancellationToken ct);
    Task<ErrorOr<UploadJobCreationResult>> ImportAsync(int platformId, Stream document, string hash, CancellationToken ct);
}

public sealed record ManagedSystem(int Id, string? SystemId, string Name, string ShortName, string? Manufacturer,
    IReadOnlyList<string> Aliases, bool Enabled, string State, string? Message, int CatalogCount, int OwnedTitles, int TrackedTitles = 0);
public sealed record SystemDatPreview(DatDocumentInspection Document, int? SuggestedPlatformId, IReadOnlyList<int> ExistingDatIds);
