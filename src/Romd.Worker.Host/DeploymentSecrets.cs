using System.Security.Cryptography;

namespace Romd.Worker.Host;

/// <summary>
///     Generates an installation's database passwords and signing secrets once and lays them out so each
///     service mounts only its own directory. <c>postgres/</c> and <c>worker/config/</c> hold the originals;
///     every other file is rewritten from them on each run, which repairs a partially initialized layout.
/// </summary>
internal static class DeploymentSecrets
{
    internal const string JwtSecretKey = "Romd__JwtSecret";
    internal const string DeliverySigningSecretKey = "Romd__ConsumerDelivery__SigningSecret";

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    // PostgreSQL runs as a different user and is the only other service mounting its directory.
    private const UnixFileMode SharedRead = OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private static readonly string[] DatabaseRoles = ["bootstrap", "provisioner", "worker", "admin", "consumer"];

    private static readonly (string Service, string[] Roles)[] ServiceRoles =
    [
        ("worker", ["worker", "provisioner"]),
        ("admin", ["admin"]),
        ("consumer", ["consumer"])
    ];

    /// <returns>The files created by this run, relative to <paramref name="root" />; never their contents.</returns>
    internal static IReadOnlyList<string> Ensure(string root)
    {
        List<string> created = [];
        Dictionary<string, string> passwords = DatabaseRoles.ToDictionary(
            role => role,
            role => EnsureSecret(root, Path.Combine("postgres", role), SharedRead, created));
        string[] sharedKeys = [JwtSecretKey, DeliverySigningSecretKey];
        Dictionary<string, string> shared = sharedKeys.ToDictionary(
            key => key,
            key => EnsureSecret(root, Path.Combine("worker", "config", key), OwnerOnly, created));

        foreach ((string service, string[] roles) in ServiceRoles)
        {
            string passFile = string.Concat(roles.Select(role => $"*:*:*:romd_{role}:{passwords[role]}\n"));
            Write(root, Path.Combine(service, "pgpass"), passFile, OwnerOnly);
            if (service == "worker")
                continue;
            foreach ((string key, string value) in shared)
                Write(root, Path.Combine(service, "config", key), value, OwnerOnly);
        }

        return created;
    }

    private static string EnsureSecret(string root, string relativePath, UnixFileMode mode, List<string> created)
    {
        string path = Path.Combine(root, relativePath);
        if (File.Exists(path))
        {
            string existing = File.ReadAllText(path).Trim();
            if (existing.Length == 0)
                throw new InvalidDataException($"{relativePath} is empty; restore it or remove it to generate a new value.");
            return existing;
        }

        // Hex stays literal in connection strings and password files.
        string secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        Write(root, relativePath, secret, mode);
        created.Add(relativePath);
        return secret;
    }

    internal static void Write(string root, string relativePath, string content, UnixFileMode mode)
    {
        string path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Replace atomically so a reader never observes a truncated secret.
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, content);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temporary, mode);
        File.Move(temporary, path, overwrite: true);
    }
}
