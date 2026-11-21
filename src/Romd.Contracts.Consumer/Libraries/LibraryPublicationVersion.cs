namespace Romd.Contracts.Consumer.Libraries;

/// <summary>A cheap change signal for one account's effective publication. Null means no available library.</summary>
public sealed record LibraryPublicationVersion(string? Version);
