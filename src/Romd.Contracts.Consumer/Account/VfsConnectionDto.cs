namespace Romd.Contracts.Consumer.Account;

public sealed record VfsConnectionDto(Guid Id, string Name, string Username, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset CredentialUpdatedAt, DateTimeOffset? LastUsedAt, string Format = "stored");

public sealed record SaveVfsConnectionRequest(string Name, string? Format = null);
public sealed record PatchVfsConnectionRequest(string? Name = null, string? Format = null);

public sealed record VfsConnectionCredentialDto(VfsConnectionDto Connection, string Password,
    string Endpoint, string RcloneConfig);
