namespace Romd.Contracts.Management.Titles;

/// <summary>
///     Partial update of one title media item. Only <c>{ "isPrimary": true }</c> is supported: it makes the item
///     the primary for its type, demoting the previous primary.
/// </summary>
public sealed record UpdateTitleMediaRequest
{
    public required bool IsPrimary { get; init; }
}
