using ErrorOr;
using Romd.Domain.Collections;

namespace Romd.Admin.Application.Collections;

internal static class CollectionInputValidation
{
    public static Error? DetailsError(string? name, string? description)
    {
        if (string.IsNullOrWhiteSpace(name))
            return CollectionErrors.EmptyName();
        if (name.Trim().Length > Collection.MaxNameLength)
            return CollectionErrors.NameTooLong();
        if (description?.Trim().Length > Collection.MaxDescriptionLength)
            return CollectionErrors.DescriptionTooLong();
        return null;
    }
}
