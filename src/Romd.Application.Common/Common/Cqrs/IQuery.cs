namespace Romd.Application.Common.Cqrs;

/// <summary>
///     Marker interface for queries.
/// </summary>
/// <typeparam name="TResult">The type of result the query returns.</typeparam>
public interface IQuery<TResult>;
