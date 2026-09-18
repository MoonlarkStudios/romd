namespace Romd.Application.Common.Configuration;

public interface IRomdOptions
{
    string DataDirectory { get; }

    // Identity (optional - defaults used if not provided)
    string? DefaultAdminEmail { get; }
    string? DefaultAdminPassword { get; }

    // Derives the OpenIddict token-encryption key (SHA256 of this value).
    string JwtSecret { get; }

    long MaxUploadBytes { get; }

    // Absolute directories that server-side path imports may read from. Empty = feature disabled.
    string[] AllowedImportPaths { get; }

    string LogsDirectory { get; }
}
