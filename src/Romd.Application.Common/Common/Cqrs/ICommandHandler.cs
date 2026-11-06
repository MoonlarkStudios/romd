using ErrorOr;

namespace Romd.Application.Common.Cqrs;

/// <summary>
///     Handles a command and returns a result.
/// </summary>
/// <typeparam name="TCommand">The type of command to handle.</typeparam>
/// <typeparam name="TResult">The type of result returned.</typeparam>
public interface ICommandHandler<in TCommand, TResult> where TCommand : ICommand<TResult>
{
    /// <summary>
    ///     Handles the command asynchronously.
    /// </summary>
    /// <param name="command">The command to handle.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result of the command, or an error.</returns>
    Task<ErrorOr<TResult>> HandleAsync(TCommand command, CancellationToken ct = default);
}
