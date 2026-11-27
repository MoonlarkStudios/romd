using ErrorOr;

namespace Romd.Consumer.Application.PlayLater;

/// <summary>Settles a staged Play Later mutation before the command acknowledges it.</summary>
public interface IPlayLaterTransaction
{
    Task<ErrorOr<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<ErrorOr<T>>> mutation, CancellationToken ct = default);
}
