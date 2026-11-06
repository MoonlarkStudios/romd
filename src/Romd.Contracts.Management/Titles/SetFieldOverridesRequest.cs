namespace Romd.Contracts.Management.Titles;

/// <summary>
///     Request to set or clear per-field source overrides for a title. A null or empty source clears the
///     field's override.
/// </summary>
public sealed record SetFieldOverridesRequest
{
    public required Dictionary<string, string?> Overrides { get; init; }
}
