using System.ComponentModel.DataAnnotations;
using Romd.Application.Common.Configuration;

namespace Romd.Host.Configuration;

/// <summary>
///     Configuration options for ROMD.
/// </summary>
public sealed class RomdOptions : IRomdOptions, IServerImportOptions
{
    public const string SectionName = "Romd";

    /// <summary>
    ///     Root directory for all ROMD data.
    ///     Defaults to ~/.local/share/romd on Linux, %APPDATA%/Romd on Windows.
    /// </summary>
    [Required]
    public string DataDirectory { get; set; } = GetDefaultDataDirectory();

    /// <summary>
    ///     Default admin email for initial setup.
    ///     Defaults to "admin@localhost" if not configured.
    /// </summary>
    public string? DefaultAdminEmail { get; set; }

    /// <summary>
    ///     Default admin password for initial setup.
    ///     Defaults to "ChangeMe123!" if not configured. Should be changed immediately.
    /// </summary>
    public string? DefaultAdminPassword { get; set; }

    /// <summary>
    ///     Secret that derives the OpenIddict token-encryption key (SHA256 of this value).
    ///     Should be at least 32 characters for security.
    /// </summary>
    [Required]
    [MinLength(32)]
    public string JwtSecret { get; set; } = "DefaultDevSecretKey_ChangeInProduction_32chars!";

    /// <summary>
    ///     Max ROM upload size in bytes. Defaults to 64 GiB and must not exceed
    ///     <c>Romd:Storage:MaxContentSize</c>.
    /// </summary>
    public long MaxUploadBytes { get; set; } = 64L * 1024 * 1024 * 1024;

    /// <summary>
    ///     Absolute directories that server-side path imports may read from.
    ///     Empty (the default) disables the import-from-path feature.
    /// </summary>
    public string[] AllowedImportPaths { get; set; } = [];

    /// <summary>The explicit drop folder used by manual checks and scheduled server imports.</summary>
    public string? ServerImportDirectory { get; set; }

    /// <summary>Optional UTC scan interval in minutes. An empty value disables the schedule.</summary>
    public int? ServerImportIntervalMinutes { get; set; }

    /// <summary>
    ///     Gets the path to log files.
    /// </summary>
    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    private static string GetDefaultDataDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Romd");
        }

        // Linux/macOS: ~/.local/share/romd
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share", "romd");
    }

    /// <summary>
    ///     Rejects relative <see cref="AllowedImportPaths" /> entries at startup so import
    ///     containment checks always compare fully qualified roots.
    /// </summary>
    public void ValidateAllowedImportPaths()
    {
        var invalidEntries = AllowedImportPaths
            .Where(entry => !Path.IsPathFullyQualified(entry))
            .ToList();

        if (invalidEntries.Count > 0)
        {
            throw new InvalidOperationException(
                "Romd:AllowedImportPaths entries must be absolute paths. " +
                $"Invalid entries: {string.Join(", ", invalidEntries.Select(entry => $"'{entry}'"))}");
        }
    }

    public void ValidateServerImportDirectory()
    {
        if (ServerImportIntervalMinutes is { } interval &&
            (string.IsNullOrWhiteSpace(ServerImportDirectory) || ServerImportSchedule.ToCron(interval) is null))
        {
            throw new InvalidOperationException(
                "Romd:ServerImportIntervalMinutes requires a server import directory and must be 5, 15, 30, or 60.");
        }

        if (string.IsNullOrWhiteSpace(ServerImportDirectory))
        {
            ServerImportDirectory = null;
            return;
        }

        if (!Path.IsPathFullyQualified(ServerImportDirectory))
            throw new InvalidOperationException("Romd:ServerImportDirectory must be an absolute path.");

        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ServerImportDirectory));
        if (!AllowedImportPaths.Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))
                .Any(root => directory.Equals(root, StringComparison.Ordinal) ||
                    directory.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Romd:ServerImportDirectory must be inside a configured Romd:AllowedImportPaths root.");
        }

        string data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DataDirectory));
        if (IsAtOrBelow(directory, data) || IsAtOrBelow(data, directory))
            throw new InvalidOperationException("Romd:ServerImportDirectory must be separate from the ROMD data directory.");

        ServerImportDirectory = directory;
    }

    private static bool IsAtOrBelow(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Ensures all required directories exist.
    /// </summary>
    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
