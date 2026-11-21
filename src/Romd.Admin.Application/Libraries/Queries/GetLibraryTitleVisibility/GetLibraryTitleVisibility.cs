using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Libraries;

namespace Romd.Admin.Application.Libraries.Queries.GetLibraryTitleVisibility;

public sealed record GetLibraryTitleVisibilityQuery(int LibraryId, int TitleId) : IQuery<LibraryTitleVisibility>;

public sealed class GetLibraryTitleVisibilityQueryHandler(ILibraryDraftEvaluator evaluator,
    IReadSnapshotTransactionFactory readSnapshots, ILogger<GetLibraryTitleVisibilityQueryHandler>? logger = null)
    : IQueryHandler<GetLibraryTitleVisibilityQuery, LibraryTitleVisibility>
{
    public Task<ErrorOr<LibraryTitleVisibility>> HandleAsync(GetLibraryTitleVisibilityQuery query, CancellationToken ct = default) =>
        readSnapshots.ExecuteReadSnapshotAsync(
            token => evaluator.GetTitleVisibilityAsync(query.LibraryId, query.TitleId, token), logger, ct);
}
