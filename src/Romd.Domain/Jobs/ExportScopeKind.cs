namespace Romd.Domain.Jobs;

/// <summary>
///     Persisted authorization boundary for an export job. A null value represents a
///     pre-scope legacy row and is never executable.
/// </summary>
public enum ExportScopeKind
{
    Invalid = 0,
    Library = 1,
    AllCatalog = 2
}
