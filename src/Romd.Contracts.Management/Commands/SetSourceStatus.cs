namespace Romd.Contracts.Management.Commands;

using Romd.Contracts.Management.Models;

/// <summary>
///     Command to transition a DAT's catalog source lifecycle status.
/// </summary>
public sealed record SetSourceStatus(SourceLifecycleStatus Status);
