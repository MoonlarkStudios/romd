using Romd.Contracts.Management.Realtime;
using Romd.Contracts.Management.Models;

namespace Romd.Infrastructure.Realtime;

public interface IAdminRealtimeEventSink
{
    Task SendJobUpdatedAsync(JobDto job, int schemaVersion, CancellationToken ct = default);

    Task SendTitleEnrichedAsync(
        AdminRealtimeTitleEnrichedOutboxPayload payload,
        int schemaVersion,
        CancellationToken ct = default);

    Task SendLibraryUpdatedAsync(
        AdminRealtimeLibraryUpdatedPayload payload,
        int schemaVersion,
        CancellationToken ct = default);

    Task SendStorageChangedAsync(int schemaVersion, CancellationToken ct = default);

    Task SendCoverageChangedAsync(int schemaVersion, CancellationToken ct = default);

    Task SendHealthChangedAsync(int schemaVersion, CancellationToken ct = default);
}
