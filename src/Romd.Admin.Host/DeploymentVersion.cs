using System.Reflection;
using System.Text.Json;
using Romd.Contracts.Management.Models;

namespace Romd.Admin.Host;

internal static class DeploymentVersion
{
    public static async Task<ServerInfo> ResolveAsync(IConfiguration configuration, ILogger logger) => new()
    {
        Release = await ReadInstalledReleaseAsync(
            configuration["Romd:DeploymentManifestPath"] ?? "/etc/romd/deployment-manifest.json", logger),
        Build = new ServerBuild
        {
            Tag = BuildMetadata("RomdReleaseTag"),
            Revision = BuildMetadata("RomdSourceRevision")
        }
    };

    private static async Task<string?> ReadInstalledReleaseAsync(string manifestPath, ILogger logger)
    {
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
            logger.LogWarning(exception, "Could not read the ROMD deployment release from {ManifestPath}", manifestPath);
        }

        return null;
    }

    private static string BuildMetadata(string key) =>
        typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == key)?.Value
        ?? throw new InvalidOperationException($"The admin host assembly is missing its {key} build metadata.");
}
