namespace Romd.Host.Configuration;

public static class ProductionSecretValidator
{
    public static void Validate(IHostEnvironment environment, RomdOptions options)
    {
        // Gate on the resolved hosting environment, not a raw env-var read: ASP.NET defaults to Production
        // when ASPNETCORE_ENVIRONMENT/DOTNET_ENVIRONMENT are unset, so reading the variable directly would
        // silently skip validation in exactly the misconfiguration (unset env) that ships the dev defaults.
        if (!environment.IsProduction())
        {
            return;
        }

        var errors = new List<string>();

        const string defaultJwtSecret = "DefaultDevSecretKey_ChangeInProduction_32chars!";
        if (options.JwtSecret == defaultJwtSecret)
        {
            errors.Add("JwtSecret is set to the default development value. Configure Romd:JwtSecret with a secure value.");
        }

        const string defaultAdminPassword = "ChangeMe123!";
        if (options.DefaultAdminPassword is null || options.DefaultAdminPassword == defaultAdminPassword)
        {
            errors.Add(
                "DefaultAdminPassword is not configured or set to the insecure default. Configure Romd:DefaultAdminPassword with a secure value.");
        }

        if (errors.Count > 0)
        {
            string message = $"""
                              SECURITY ERROR: Cannot start in Production environment with insecure configuration.

                              {string.Join(Environment.NewLine, errors.Select(e => $"  - {e}"))}

                              To fix:
                              1. Set environment variables or configure appsettings.Production.json
                              2. Use strong, unique values for all secrets
                              """;

            throw new InvalidOperationException(message);
        }
    }
}
