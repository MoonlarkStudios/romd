using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Romd.Persistence.Identity;

namespace Romd.Infrastructure.Identity;

internal interface IConsumerPasswordPolicy
{
    IdentityResult Validate(RomdUser user, string password);
}

internal sealed class ConsumerPasswordPolicy(
    IOptions<IdentityOptions> identityOptions,
    IdentityErrorDescriber errorDescriber)
    : IConsumerPasswordPolicy
{
    public IdentityResult Validate(RomdUser user, string password)
    {
        var options = identityOptions.Value.Password;
        List<IdentityError> errors = [];

        if (string.IsNullOrWhiteSpace(password) || password.Length < options.RequiredLength)
        {
            errors.Add(errorDescriber.PasswordTooShort(options.RequiredLength));
        }

        if (options.RequireNonAlphanumeric && password.All(char.IsLetterOrDigit))
        {
            errors.Add(errorDescriber.PasswordRequiresNonAlphanumeric());
        }

        if (options.RequireDigit && !password.Any(char.IsDigit))
        {
            errors.Add(errorDescriber.PasswordRequiresDigit());
        }

        if (options.RequireLowercase && !password.Any(char.IsLower))
        {
            errors.Add(errorDescriber.PasswordRequiresLower());
        }

        if (options.RequireUppercase && !password.Any(char.IsUpper))
        {
            errors.Add(errorDescriber.PasswordRequiresUpper());
        }

        if (options.RequiredUniqueChars >= 1 && password.Distinct().Count() < options.RequiredUniqueChars)
        {
            errors.Add(errorDescriber.PasswordRequiresUniqueChars(options.RequiredUniqueChars));
        }

        return errors.Count == 0
            ? IdentityResult.Success
            : IdentityResult.Failed(errors.ToArray());
    }
}
