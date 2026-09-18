namespace Romd.Contracts.Consumer.Account;

public sealed record ChangePasswordRequest(
    string CurrentPassword,
    string NewPassword);
