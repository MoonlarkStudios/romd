using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Romd.Mount.Credentials;

public sealed class MountCredentialScope
{
    public string Key { get; }
    public Guid InstanceId { get; }
    public Uri Origin { get; }
    public string Profile { get; }

    public MountCredentialScope(BoundMountIdentity identity, string profile)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(profile) || profile.Length > 128 || profile.Any(char.IsControl))
            throw new ArgumentException("A local profile identifier is required.", nameof(profile));
        InstanceId = identity.InstanceId;
        Origin = identity.Origin;
        Profile = profile;
        // Unambiguous encoding; origin changes require a new credential selection too.
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { Origin.AbsoluteUri, InstanceId.ToString("D"), profile }));
        Key = Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}

public interface IMountCredentialStore
{
    Task<string?> ReadAsync(MountCredentialScope scope, CancellationToken ct = default);
    Task WriteAsync(MountCredentialScope scope, string refreshToken, CancellationToken ct = default);
    Task DeleteAsync(MountCredentialScope scope, CancellationToken ct = default);
}

public sealed class MountCredentialException(int status) : Exception($"Credential storage failed (OS status {status}).")
{
    public int Status { get; } = status;
}
