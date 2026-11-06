namespace Romd.Admin.Application.Export;

/// <summary>
///     Defines the catalog boundary an export may read. The variants are explicit so an absent
///     library can never be interpreted as permission to export the entire catalog.
/// </summary>
public abstract record ExportScope
{
    private ExportScope()
    {
    }

    public sealed record Library(int LibraryId, long MaterializationGeneration) : ExportScope;

    public sealed record AllCatalog : ExportScope;
}

/// <summary>
///     Carries the explicit scope together with the persisted user assignment that must still
///     hold when a synchronous title export selects files. Null means an administrator authorized
///     the scope and no user-library correlation is required.
/// </summary>
public sealed record AuthorizedExportScope(
    ExportScope Scope,
    Guid? EffectiveLibraryUserId);
