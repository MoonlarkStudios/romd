namespace Romd.Contracts.Management.Models;

public sealed record SourceRemovalImpact(string Name, string Action, string ReviewToken, int Versions,
    int StillCovered, int OwnedWithoutDefinition, int PersonalWithoutDefinition, int CatalogOnlyWithoutDefinition,
    bool Busy);
public sealed record ApplySourceLifecycle(string Action, string ReviewToken);
