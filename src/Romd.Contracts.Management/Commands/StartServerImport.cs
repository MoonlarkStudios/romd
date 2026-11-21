namespace Romd.Contracts.Management.Commands;

/// <summary>Check the configured drop folder or retry the surviving files from its last failed import.</summary>
public sealed record StartServerImport(Guid? RetryJobId = null);
