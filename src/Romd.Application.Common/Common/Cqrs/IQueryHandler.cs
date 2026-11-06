using ErrorOr;

namespace Romd.Application.Common.Cqrs;

/// <summary>
///     Handles a query and returns a result.
/// </summary>
/// <typeparam name="TQuery">The type of query to handle.</typeparam>
/// <typeparam name="TResult">The type of result returned.</typeparam>
public interface IQueryHandler<in TQuery, TResult> where TQuery : IQuery<TResult>
{
    /// <summary>
    ///     Handles the query asynchronously.
    /// </summary>
    /// <param name="query">The query to handle.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result of the query, or an error.</returns>
    Task<ErrorOr<TResult>> HandleAsync(TQuery query, CancellationToken ct = default);
}
