using Microsoft.AspNetCore.Identity;

namespace Romd.Infrastructure.Identity;

public static class RomdIdentityOptions
{
    public static void Configure(IdentityOptions options)
    {
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredLength = 8;
        options.User.RequireUniqueEmail = true;
    }
}
