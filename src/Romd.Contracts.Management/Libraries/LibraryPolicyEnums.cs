namespace Romd.Contracts.Management.Libraries;

/// <summary>
///     How titles enter a library. Serialized as a named string and mirrored from
///     <c>Romd.Domain.Libraries.LibraryTitleSelectionMode</c> at the application boundary.
/// </summary>
public enum LibraryTitleSelectionMode
{
    Rules = 0,
    IncludeOnly = 1
}

/// <summary>
///     How multiple content ratings select the policy basis. Serialized as a named string and
///     mirrored from <c>Romd.Domain.Libraries.RatingBasisSelection</c>.
/// </summary>
public enum RatingBasisSelection
{
    Strictest = 0,
    Preferred = 1
}

/// <summary>
///     How missing metadata affects library eligibility. Serialized as a named string and
///     mirrored from <c>Romd.Domain.Libraries.UnknownMetadataPolicy</c>.
/// </summary>
public enum UnknownMetadataPolicy
{
    Allow = 0,
    Hide = 1,
    NeedsReview = 2
}
