namespace Romd.Admin.Application.Diagnostics;

public interface IOperationalDiagnosticsReader
{
    Task<BoundedDiagnosticsData<CatalogProjectionDiagnosticsData>> ReadCatalogProjectionsAsync(
        int limit,
        CancellationToken ct = default);

    Task<OperationalJobDiagnosticsData> ReadJobsAsync(int limit, CancellationToken ct = default);

    Task<OutboxDiagnosticsData> ReadOutboxAsync(CancellationToken ct = default);
}

public interface IHangfireDiagnosticsReader
{
    Task<HangfireDiagnosticsData> ReadAsync(
        int serverLimit,
        int recurringJobLimit,
        CancellationToken ct = default);
}

public interface IStorageDiagnosticsReader
{
    Task<StorageDiagnosticsData> ReadAsync(CancellationToken ct = default);
}
