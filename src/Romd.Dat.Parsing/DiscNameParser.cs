using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Romd.Dat.Parsing.Models;

namespace Romd.Dat.Parsing;

/// <summary>
///     Parses a bounded Disc N marker without discarding edition qualifiers. Independent of
///     title normalization: removing region/revision/unknown tags would merge distinct editions.
/// </summary>
public static partial class DiscNameParser
{
    public const string RuleVersion = "dat-disc-name/v2";
    public const int MaxNameLength = 500;
    public const int MaxDiscNumber = 99;

    public static ParsedDiscName Parse(string? name)
    {
        string original = name ?? string.Empty;
        ParsedDiscName Review(DiscNameIssue issue) => new(original, DiscNameStatus.NeedsReview, issue);

        if (string.IsNullOrWhiteSpace(original))
            return Review(DiscNameIssue.EmptyName);
        if (original.Length > MaxNameLength)
            return Review(DiscNameIssue.NameTooLong);
        if (!PotentialMarker().IsMatch(original) && !PotentialRoleQualifier().IsMatch(original))
            return new(original, DiscNameStatus.NoDiscMarker);

        // Validate and locate qualifiers before removing anything, including malformed nesting.
        var qualifiers = new List<(int Start, int Length, string Text)>();
        int start = -1;
        for (int index = 0; index < original.Length; index++)
        {
            if (original[index] == '(')
            {
                if (start >= 0)
                    return Review(DiscNameIssue.MalformedQualifiers);
                start = index;
            }
            else if (original[index] == ')')
            {
                if (start < 0)
                    return Review(DiscNameIssue.MalformedQualifiers);
                qualifiers.Add((start, index - start + 1, original[(start + 1)..index]));
                start = -1;
            }
        }
        if (start >= 0)
            return Review(DiscNameIssue.MalformedQualifiers);

        bool hasRoleQualifier = qualifiers.Any(q => RoleQualifier().IsMatch(q.Text));
        var markers = qualifiers.Where(q => MarkerPrefix().IsMatch(q.Text)).ToList();
        if (markers.Count > 1)
            return Review(DiscNameIssue.MultipleDiscMarkers);
        if (markers.Count == 0)
        {
            return hasRoleQualifier || UnparenthesizedMarker().IsMatch(original)
                ? Review(DiscNameIssue.UnsupportedDiscMarker)
                : new(original, DiscNameStatus.NoDiscMarker);
        }

        var marker = markers[0];
        var match = DiscMarker().Match(marker.Text);
        if (!match.Success)
            return Review(DiscNameIssue.UnsupportedDiscMarker);
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            || number is < 1 or > MaxDiscNumber)
            return Review(DiscNameIssue.DiscNumberOutOfRange);

        int? total = null;
        if (match.Groups[2].Success)
        {
            if (!int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int count)
                || count < number || count > MaxDiscNumber)
                return Review(DiscNameIssue.InvalidDiscTotal);
            total = count;
        }

        if (hasRoleQualifier)
            return Review(DiscNameIssue.MultipleDiscMarkers);

        string edition = Whitespace().Replace(original.Remove(marker.Start, marker.Length).Trim(), " ");
        if (UnparenthesizedMarker().IsMatch(edition))
            return Review(DiscNameIssue.MultipleDiscMarkers);
        // Require title text before the qualifier list; tags alone are not an edition.
        int firstQualifier = edition.IndexOf('(');
        string title = firstQualifier >= 0 ? edition[..firstQualifier] : edition;
        if (string.IsNullOrWhiteSpace(title))
            return Review(DiscNameIssue.MissingTitle);

        return new(original, DiscNameStatus.Parsed, EditionName: edition,
            GroupingKey: edition.ToUpperInvariant(), DiscNumber: number, DeclaredDiscCount: total)
        {
            Qualifiers = qualifiers.Where(q => q.Start != marker.Start).Select(q => q.Text).ToImmutableArray()
        };
    }

    [GeneratedRegex(@"^\s*(?:discs?|disks?|cd)(?:\s|[0-9]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex MarkerPrefix();

    [GeneratedRegex(@"\(\s*(?:discs?|disks?|cd)(?:\s|[0-9]|\)|$)|\b(?:disc|disk|cd)\s+[0-9]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex PotentialMarker();

    // Modifier-first role names remain visible without assigning an ordinal or stripping their edition.
    [GeneratedRegex(@"\([^()]*\S\s+(?:discs?|disks?|cd)(?:\s+[0-9]+)?\s*(?:\)|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex PotentialRoleQualifier();

    [GeneratedRegex(@"^\s*\S[^()]*\s+(?:discs?|disks?|cd)(?:\s+[0-9]+)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex RoleQualifier();

    [GeneratedRegex(@"^\s*Disc\s+([0-9]+)(?:\s+of\s+([0-9]+))?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex DiscMarker();

    [GeneratedRegex(@"\b(?:disc|disk|cd)\s+[0-9]+\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex UnparenthesizedMarker();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Whitespace();
}
