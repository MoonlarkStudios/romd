using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Libraries;
using Romd.Domain.Libraries;

using LibraryConfiguration = Romd.Domain.Libraries.LibraryConfiguration;

namespace Romd.Admin.Application.Libraries.Queries.EvaluateLibrary;

public sealed record EvaluateLibraryQuery(int LibraryId, LibraryConfiguration Configuration,
    string View, string? Search, int AfterId, string? Cursor = null, int Limit = 48) : IQuery<LibraryEvaluation>;

public sealed class EvaluateLibraryQueryHandler(ILibraryDraftEvaluator evaluator)
    : IQueryHandler<EvaluateLibraryQuery, LibraryEvaluation>
{
    public Task<ErrorOr<LibraryEvaluation>> HandleAsync(EvaluateLibraryQuery query, CancellationToken ct = default) =>
        evaluator.EvaluateAsync(query.LibraryId, query.Configuration, query.View, query.Search, query.AfterId, ct, query.Cursor, query.Limit);
}
