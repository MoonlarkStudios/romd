using ErrorOr;

namespace Romd.Application.Common.Security;

/// <summary>
/// Owns one shared authentication-session commit. Its database-only callback must not nest
/// a transaction or issue cookies/tokens. Results become observable only after commit.
/// </summary>
public interface IAccountSessionTransaction
{
    Task<ErrorOr<AccountSession>> ExecuteAsync(
        Func<CancellationToken, Task<ErrorOr<AccountSession>>> mutation,
        CancellationToken ct = default);
}
