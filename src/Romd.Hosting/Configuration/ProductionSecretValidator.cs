using Romd.Infrastructure.Delivery;

namespace Romd.Host.Configuration;

public static class ProductionSecretValidator
{
    public static void Validate(IHostEnvironment environment, RomdOptions options, IConfiguration configuration)
    {
        // Gate on the resolved hosting environment, not a raw env-var read: ASP.NET defaults to Production
        // when ASPNETCORE_ENVIRONMENT/DOTNET_ENVIRONMENT are unset, so reading the variable directly would
        // silently skip validation in exactly the misconfiguration (unset env) that ships the dev defaults.
        if (!environment.IsProduction())
        {
            return;
        }

        var errors = new List<string>();

        // Exact matches only: the legacy built-in defaults and the placeholders published in the root
        // .env.example. Minimum lengths are enforced by the options themselves.
        string[] developmentJwtSecrets =
            ["DefaultDevSecretKey_ChangeInProduction_32chars!", "local-only-replace-this-jwt-secret-32-plus-chars"];
        if (developmentJwtSecrets.Contains(options.JwtSecret))
        {
            errors.Add("JwtSecret is set to the default development value. Configure Romd:JwtSecret with a secure value.");
        }

        string[] developmentAdminPasswords = ["ChangeMe123!", "LocalOnly123"];
        if (options.DefaultAdminPassword is null || developmentAdminPasswords.Contains(options.DefaultAdminPassword))
        {
            errors.Add(
                "DefaultAdminPassword is not configured or set to the insecure default. Configure Romd:DefaultAdminPassword with a secure value.");
        }

        const string developmentDeliverySigningSecret = "local-only-replace-this-delivery-secret-32-bytes-min";
        string? deliverySigningSecret = configuration
            .GetSection(ConsumerDeliveryOptions.SectionName)[nameof(ConsumerDeliveryOptions.SigningSecret)];
        if (deliverySigningSecret == developmentDeliverySigningSecret)
        {
            errors.Add(
                "ConsumerDelivery:SigningSecret is set to the default development value. Configure Romd:ConsumerDelivery:SigningSecret with a secure value.");
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
