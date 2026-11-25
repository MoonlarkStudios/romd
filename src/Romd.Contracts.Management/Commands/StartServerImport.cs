namespace Romd.Contracts.Management.Commands;

/// <summary>Check the configured drop folder or retry the reviewed originals from a retained import.</summary>
/// <param name="RetryJobId">The original server folder job when retrying.</param>
/// <param name="ReviewedRevision">Binds a retry to the file observation returned by its review.</param>
public sealed record StartServerImport(Guid? RetryJobId = null, string? ReviewedRevision = null);
