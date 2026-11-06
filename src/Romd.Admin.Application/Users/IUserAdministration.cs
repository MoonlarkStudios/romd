using ErrorOr;
using Romd.Domain.Identity;

namespace Romd.Admin.Application.Users;

/// <summary>
///     Application-owned boundary for administrative identity workflows. Implementations may flush
///     individual Identity operations, but never own or commit the surrounding transaction.
/// </summary>
public interface IUserAdministration
{
    Task<ErrorOr<ManagedUser>> CreatePendingAsync(string email, int? libraryId, DateTimeOffset createdAt, CancellationToken ct = default);

    Task<IReadOnlyList<ManagedUser>> ListAsync(CancellationToken ct = default);
    Task<ManagedUser?> GetByIdAsync(Guid userId, CancellationToken ct = default);
    Task<ManagedUser?> GetByEmailAsync(string email, CancellationToken ct = default);
    Task<ErrorOr<ManagedUser>> CreateAsync(
        string email,
        string password,
        int? libraryId,
        DateTimeOffset createdAt,
        CancellationToken ct = default);
    Task<ErrorOr<ManagedUser>> AddRoleAsync(
        Guid userId,
        RomdRoleType role,
        CancellationToken ct = default);
    Task<ErrorOr<ManagedUser>> UpdateAsync(
        Guid userId,
        string? email,
        string? newPassword,
        DateTimeOffset updatedAt,
        CancellationToken ct = default);
    Task<ErrorOr<ManagedUser>> ReplaceRoleAsync(
        Guid userId,
        RomdRoleType role,
        DateTimeOffset updatedAt,
        CancellationToken ct = default);
    Task<ErrorOr<ManagedUser>> AssignLibraryAsync(
        Guid userId,
        int? libraryId,
        DateTimeOffset updatedAt,
        CancellationToken ct = default);
    Task<ErrorOr<Deleted>> DeleteAsync(Guid userId, CancellationToken ct = default);
    Task<int> AssignDefaultLibraryAsync(
        int libraryId,
        DateTimeOffset updatedAt,
        CancellationToken ct = default);
}
