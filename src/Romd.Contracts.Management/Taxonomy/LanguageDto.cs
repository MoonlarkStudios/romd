using Romd.Contracts.Common.Models;

namespace Romd.Contracts.Management.Taxonomy;

/// <summary>
///     The admin management projection of a language: every row, registered (<c>ownership</c> is <c>Romd</c>) or
///     created from a DAT token (<c>ownership</c> is null and the key is in the reserved <c>dat-</c> namespace).
/// </summary>
public sealed class LanguageDto
{
    public required string Key { get; init; }
    public required string? Ownership { get; init; }
    public required int? BuiltInVersion { get; init; }
    public required string Name { get; init; }
    public required string? Description { get; init; }
    public required int SortOrder { get; init; }
    public required string Code { get; init; }
    public required bool Retired { get; init; }
    public required bool IsAutoCreated { get; init; }

    /// <summary>Only unregistered rows merge into another row.</summary>
    public required bool CanMerge { get; init; }

    /// <summary>
    ///     The language's first aliases in ascending id order; <c>nextCursor</c> continues them through
    ///     <c>/api/languages/{languageKey}/aliases</c>.
    /// </summary>
    public required Page<AliasDto> Aliases { get; init; }
}
