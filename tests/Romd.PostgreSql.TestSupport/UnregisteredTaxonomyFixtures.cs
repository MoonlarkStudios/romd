using Romd.Domain.Taxonomy;

namespace Romd.PostgreSql.TestSupport;

/// <summary>
///     Keys for unregistered region and language rows a test inserts directly, minted from the row's name the way DAT
///     ingest mints them from the token that created the row. Never used by runtime contexts.
/// </summary>
public static class UnregisteredTaxonomyFixtures
{
    public static string KeyFor(string name) => UnregisteredTaxonomyKey.Candidates(name.Trim().ToLowerInvariant()).First();
}
