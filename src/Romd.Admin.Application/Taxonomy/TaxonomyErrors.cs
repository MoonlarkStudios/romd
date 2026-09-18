using ErrorOr;

namespace Romd.Admin.Application.Taxonomy;

public static class TaxonomyErrors
{
    public static Error EntityNotFound(string entityType, int id) =>
        Error.NotFound("Taxonomy.EntityNotFound", $"{entityType} with ID {id} was not found.");

    public static readonly Error MergeIntoSelf =
        Error.Validation("Taxonomy.MergeIntoSelf", "Cannot merge a region or language into itself.");

    public static Error AliasAlreadyExists(string alias) =>
        Error.Conflict("Taxonomy.AliasAlreadyExists", $"Alias '{alias}' is already in use.");

    public static Error AliasNotFound(int id) =>
        Error.NotFound("Taxonomy.AliasNotFound", $"Alias with ID {id} was not found.");
}
