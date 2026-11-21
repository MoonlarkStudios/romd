using ErrorOr;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Libraries;

namespace Romd.Admin.Application.Libraries.Queries.GetLibraryPublicationStatus;

public sealed record GetLibraryPublicationStatusQuery(int LibraryId) : IQuery<LibraryPublicationStatus>;

public sealed class GetLibraryPublicationStatusQueryHandler(ILibraryRepository libraries, IMaterializationJobRepository jobs)
    : IQueryHandler<GetLibraryPublicationStatusQuery, LibraryPublicationStatus>
{
    public async Task<ErrorOr<LibraryPublicationStatus>> HandleAsync(GetLibraryPublicationStatusQuery query, CancellationToken ct = default)
    {
        if (await libraries.GetByIdAsync(query.LibraryId, ct) is null)
            return LibraryErrors.NotFound();
        var latest = await jobs.GetLatestForLibraryAsync(query.LibraryId, ct);
        return new LibraryPublicationStatus(latest is null ? null : new LibraryPublicationAttempt(
            latest.Id, latest.Phase, latest.IsTerminal, latest.Outcome?.ToString(),
            latest.CreatedAt, latest.CompletedAt, latest.IncludedCount, latest.ExcludedCount));
    }
}
