using System.Reflection;
using System.Text.Json;

namespace Romd.Admin.Host;

internal static class DeploymentVersion
{
    public static async Task<string> ResolveAsync(IConfiguration configuration, ILogger logger)
    {
        string manifestPath = configuration["Romd:DeploymentManifestPath"] ?? "/etc/romd/deployment-manifest.json";
        try
        {
            await using var stream = File.OpenRead(manifestPath);
            using var manifest = await JsonDocument.ParseAsync(stream);
            if (manifest.RootElement.ValueKind == JsonValueKind.Object
                && manifest.RootElement.TryGetProperty("releaseTag", out var tag)
                && tag.ValueKind == JsonValueKind.String
                && tag.GetString() is { } releaseTag && !string.IsNullOrWhiteSpace(releaseTag))
            {
                return releaseTag;
            }

            logger.LogWarning("The ROMD deployment manifest at {ManifestPath} is missing its release tag", manifestPath);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Source builds and standalone images have no deployment manifest.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(exception, "Could not read the ROMD deployment version from {ManifestPath}", manifestPath);
        }

        return typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "RomdReleaseTag")?.Value
            ?? throw new InvalidOperationException("The admin host assembly is missing its ROMD release tag.");
    }
}
