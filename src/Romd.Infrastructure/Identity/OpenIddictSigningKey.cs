using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Romd.Infrastructure.Identity;

/// <summary>
///     Owns the OpenIddict RSA signing key persisted under the data directory. The worker creates
///     it; API hosts load it (fail-fast if absent). Creation is atomic so concurrent creators
///     converge on a single key. Centralized so creation and permission hardening cannot drift.
/// </summary>
public static class OpenIddictSigningKey
{
    public const string KeyId = "romd-openiddict-signing";
    private const string FileName = "openiddict-signing.pem";

    public static string ResolvePath(string dataDirectory) =>
        Path.Combine(dataDirectory, "keys", FileName);

    /// <summary>Creates the signing key if absent. Safe to call concurrently.</summary>
    public static void EnsureCreated(string dataDirectory)
    {
        string keyPath = ResolvePath(dataDirectory);
        if (File.Exists(keyPath))
        {
            Harden(keyPath);
            return;
        }

        string keyDirectory = Path.GetDirectoryName(keyPath)!;
        Directory.CreateDirectory(keyDirectory);

        using var rsa = RSA.Create(2048);
        string tempPath = Path.Combine(keyDirectory, $"{FileName}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, rsa.ExportPkcs8PrivateKeyPem());
        Harden(tempPath);

        try
        {
            // Atomic on the same volume; throws if another creator already won the race.
            File.Move(tempPath, keyPath);
        }
        catch (IOException) when (File.Exists(keyPath))
        {
            File.Delete(tempPath);
        }
    }

    /// <summary>Loads the signing key. Throws if it has not been created yet.</summary>
    public static RsaSecurityKey Load(string dataDirectory)
    {
        string keyPath = ResolvePath(dataDirectory);
        if (!File.Exists(keyPath))
        {
            throw new InvalidOperationException(
                $"OpenIddict signing key not found at '{keyPath}'. Ensure the worker host has " +
                "completed first-run initialization before starting API hosts.");
        }

        Harden(keyPath);
        var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(keyPath));
        return new RsaSecurityKey(rsa) { KeyId = KeyId };
    }

    private static void Harden(string keyPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
