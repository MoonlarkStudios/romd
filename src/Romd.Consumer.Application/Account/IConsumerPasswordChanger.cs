using ErrorOr;

namespace Romd.Consumer.Application.Account;

public interface IConsumerPasswordChanger
{
    /// <summary>
    ///     Verifies the current password and prepares its replacement outside a transaction.
    ///     The opaque receipt retains the verified snapshot without exposing credentials or hashes.
    /// </summary>
    Task<ErrorOr<IPreparedConsumerPasswordChange>> PrepareAsync(
        Guid userId,
        string currentPassword,
        string newPassword,
        CancellationToken ct = default);
}

/// <summary>A scoped prepared change; apply inside the caller's security transaction, then commit.</summary>
public interface IPreparedConsumerPasswordChange
{
    /// <summary>Revalidates the verified snapshot and stages credentials and session revocation without committing.</summary>
    Task<ErrorOr<Updated>> ApplyStagedAsync(CancellationToken ct = default);
}
