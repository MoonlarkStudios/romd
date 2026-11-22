using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Libraries;

namespace Romd.Admin.Application.Libraries.Queries.ReviewLibraryPublication;

public sealed record ReviewLibraryPublicationQuery(int LibraryId) : IQuery<LibraryPublicationReview>;

public sealed class ReviewLibraryPublicationQueryHandler(ILibraryDraftEvaluator evaluator, IReadSnapshotTransactionFactory snapshots,
    ILogger<ReviewLibraryPublicationQueryHandler> logger) : IQueryHandler<ReviewLibraryPublicationQuery, LibraryPublicationReview>
{
    public Task<ErrorOr<LibraryPublicationReview>> HandleAsync(ReviewLibraryPublicationQuery query, CancellationToken ct = default) =>
        snapshots.ExecuteReadSnapshotAsync(token => evaluator.ReviewPublicationAsync(query.LibraryId, token), logger, ct);
}
