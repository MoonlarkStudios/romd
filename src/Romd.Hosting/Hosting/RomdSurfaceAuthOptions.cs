namespace Romd.Hosting;

/// <summary>
///     Per-host OpenIddict surface binding. All clients live in one shared applications table, so
///     each host must reject client IDs that do not belong to its surface and always stamp tokens
///     with its own audience — never derive the audience from the requested client.
/// </summary>
public sealed class RomdSurfaceAuthOptions
{
    public required string Audience { get; init; }

    public required IReadOnlySet<string> AllowedClientIds { get; init; }

    public bool IsClientAllowed(string? clientId) =>
        clientId is not null && AllowedClientIds.Contains(clientId);
}
