using Microsoft.AspNetCore.Identity;
using Romd.Persistence.Identity;

namespace Romd.Infrastructure.Identity;

/// <summary>One password length bound for Identity mutations, consumer changes and interactive login.</summary>
public sealed class RomdPasswordLengthValidator : IPasswordValidator<RomdUser>
{
    public const int MaximumLength = 4096;

    public Task<IdentityResult> ValidateAsync(UserManager<RomdUser> manager, RomdUser user, string? password) =>
        Task.FromResult(ValidateLength(password));

    public static IdentityResult ValidateLength(string? password) =>
        password?.Length > MaximumLength
            ? IdentityResult.Failed(new IdentityError
            {
                Code = "PasswordTooLong",
                Description = $"Passwords must be at most {MaximumLength} characters."
            })
            : IdentityResult.Success;
}
