using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Libraries;
using Romd.Domain.Libraries;

namespace Romd.Admin.Application.Libraries.Queries.EvaluateLibrary;

public sealed record EvaluateLibraryQuery(int LibraryId, LibraryConfiguration Configuration,
    string View, string? Search, int AfterId) : IQuery<LibraryEvaluationDto>;

public sealed class EvaluateLibraryQueryHandler(ILibraryDraftEvaluator evaluator)
    : IQueryHandler<EvaluateLibraryQuery, LibraryEvaluationDto>
{
    public Task<ErrorOr<LibraryEvaluationDto>> HandleAsync(EvaluateLibraryQuery query, CancellationToken ct = default) =>
        evaluator.EvaluateAsync(query.LibraryId, query.Configuration, query.View, query.Search, query.AfterId, ct);
}
