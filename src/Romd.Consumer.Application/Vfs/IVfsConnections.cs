using ErrorOr;
using Romd.Domain.Vfs;
using Romd.Contracts.Consumer.Account;
using Romd.Contracts.Common.Models;

namespace Romd.Consumer.Application.Vfs;

public interface IVfsConnections
{
    Task<IReadOnlyList<VfsConnectionDto>> ListAsync(Guid userId, CancellationToken ct);
    Task<ErrorOr<Page<VfsConnectionDto>>> ListPageAsync(Guid userId, string? cursor, int limit, CancellationToken ct);
    /// <summary>Stages the credential mutation in the caller's required active transaction; does not flush or commit.</summary>
    Task<ErrorOr<IssuedVfsCredential>> CreateAsync(Guid userId, string name, CancellationToken ct, VfsFormat format = VfsFormat.Stored);
    Task<ErrorOr<VfsConnectionDto>> RenameAsync(Guid userId, Guid id, string? name, CancellationToken ct, VfsFormat? format = null);
    /// <summary>Stages the credential mutation in the caller's required active transaction; does not flush or commit.</summary>
    Task<ErrorOr<IssuedVfsCredential>> RotateAsync(Guid userId, Guid id, CancellationToken ct);
    /// <summary>Stages the credential mutation in the caller's required active transaction; does not flush or commit.</summary>
    Task<ErrorOr<Success>> RevokeAsync(Guid userId, Guid id, CancellationToken ct);
    Task<AuthenticatedVfsConnection?> AuthenticateAsync(string username, string password, CancellationToken ct);
}

public sealed record AuthenticatedVfsConnection(Guid UserId, Guid ConnectionId, VfsFormat Format = VfsFormat.Stored);

// Only issuance returns a secret. Listing and authentication cannot recover one.
public sealed record IssuedVfsCredential(VfsConnectionDto Connection, string Password);

public static class VfsConnectionErrors
{
    public static Error InvalidFormat() => Error.Validation("Vfs.InvalidFormat", "Choose stored or original.");
    public static Error NotFound() => Error.NotFound("Vfs.ConnectionNotFound", "Connection not found.");
    public static Error InvalidName() => Error.Validation("Vfs.InvalidConnectionName", "Use a name of 1–120 characters without control characters.");
    public static Error LimitReached() => Error.Conflict("Vfs.ConnectionLimit", "Revoke an existing connection before creating another (maximum 32 active connections).");
    public static Error Revoked() => Error.Conflict("Vfs.ConnectionRevoked", "This connection has been revoked. Create a new connection.");
    public static bool IsValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 120 && !name.Any(char.IsControl);
}
