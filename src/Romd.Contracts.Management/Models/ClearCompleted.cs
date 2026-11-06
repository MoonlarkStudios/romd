namespace Romd.Contracts.Management.Models;

/// <summary>
///     Result of clearing completed jobs.
/// </summary>
public sealed record ClearCompleted(int ArchivedCount);
