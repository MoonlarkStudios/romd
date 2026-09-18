namespace Romd.Contracts.Management.Models;

/// <summary>
///     Result of a source lifecycle transition.
/// </summary>
public sealed record SourceStatus
{
    /// <summary>
    ///     The source's status after the call.
    /// </summary>
    public required SourceLifecycleStatus Status { get; init; }

    /// <summary>
    ///     Whether this call changed the status (false when it already held).
    /// </summary>
    public required bool Changed { get; init; }
}
