using System.Collections.Immutable;

namespace Romd.Dat.Parsing.Models;

public enum DiscNameStatus { NoDiscMarker, Parsed, NeedsReview }

public enum DiscNameIssue
{
    EmptyName,
    NameTooLong,
    MalformedQualifiers,
    UnsupportedDiscMarker,
    MultipleDiscMarkers,
    DiscNumberOutOfRange,
    InvalidDiscTotal,
    MissingTitle
}

/// <summary>Name observations only; no catalog assignment, media identity or completeness claim.</summary>
public sealed record ParsedDiscName(
    string OriginalName,
    DiscNameStatus Status,
    DiscNameIssue? Issue = null,
    string? EditionName = null,
    string? GroupingKey = null,
    int? DiscNumber = null,
    int? DeclaredDiscCount = null)
{
    public ImmutableArray<string> Qualifiers { get; init; } = [];
}
