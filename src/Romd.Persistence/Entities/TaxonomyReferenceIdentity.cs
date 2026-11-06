namespace Romd.Persistence.Entities;

/// <summary>
///     The two states of a region or language row. An unregistered row has no ownership or base facts and a key
///     minted in the reserved <c>dat-</c> namespace (<see cref="Romd.Domain.Taxonomy.UnregisteredTaxonomyKey" />); a
///     registered row has ownership, base facts and a key outside that namespace.
/// </summary>
internal static class TaxonomyReferenceIdentity
{
    internal const string Check =
        "(\"Ownership\" IS NULL AND \"CanonicalKey\" ~ '^dat-[a-z0-9]+(-[a-z0-9]+)*$' AND \"BaseName\" IS NULL AND \"BaseDescription\" IS NULL AND NOT \"Retired\" AND \"BaseSortOrder\" = 0) OR (\"Ownership\" IS NOT NULL AND \"CanonicalKey\" !~ '^dat-' AND \"BaseName\" IS NOT NULL)";
}
