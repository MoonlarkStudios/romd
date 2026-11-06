using ErrorOr;

namespace Romd.Admin.Application.Source.Dat;

public sealed record PublishedDatCatalog(string CatalogId, string SystemId, string Name, string Provider,
    string Health, string? DocumentHash, int EntryCount, int FileCount, DateTimeOffset? LastChangedAt);

public interface ISignedDatCatalogClient
{
    bool Enabled { get; }
    Task<ErrorOr<IReadOnlyList<PublishedDatCatalog>>> DiscoverAsync(CancellationToken ct);
    Task<ErrorOr<byte[]>> FetchAsync(string catalogId, string systemId, string expectedName, CancellationToken ct);
}
