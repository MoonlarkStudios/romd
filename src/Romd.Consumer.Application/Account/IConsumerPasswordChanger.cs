using ErrorOr;

namespace Romd.Consumer.Application.Account;

public interface IConsumerPasswordChanger
{
    Task<ErrorOr<Updated>> ChangePasswordAsync(
        Guid userId,
        string currentPassword,
        string newPassword,
        CancellationToken ct = default);
}
