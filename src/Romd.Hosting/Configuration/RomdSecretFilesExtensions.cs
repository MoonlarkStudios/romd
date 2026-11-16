using Microsoft.Extensions.Configuration;

namespace Romd.Host.Configuration;

public static class RomdSecretFilesExtensions
{
    /// <summary>Names the directory of one-value-per-file settings written by deployment initialization.</summary>
    public const string DirectoryVariable = "ROMD_SECRETS_DIRECTORY";

    /// <summary>
    ///     Loads settings from files named after their configuration key (<c>Romd__JwtSecret</c>), so secrets
    ///     generated at first start never pass through the environment. A configured directory must exist:
    ///     starting without it would silently fall back to weaker or missing values.
    /// </summary>
    public static IConfigurationBuilder AddRomdSecretFiles(this IConfigurationBuilder builder, string? directory) =>
        string.IsNullOrWhiteSpace(directory)
            ? builder
            : builder.AddKeyPerFile(directory, optional: false);
}
