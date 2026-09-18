using ErrorOr;

namespace Romd.Application.Common.Cqrs;

/// <summary>
///     Marker interface for commands that return a result.
/// </summary>
/// <typeparam name="TResult">The type of result the command returns.</typeparam>
public interface ICommand<TResult>;

/// <summary>
///     Marker interface for commands that don't return a meaningful result.
///     Returns <see cref="Deleted"/> as a placeholder.
/// </summary>
public interface ICommand : ICommand<Deleted>;
