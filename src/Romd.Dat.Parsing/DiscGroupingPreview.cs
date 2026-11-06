using System.Collections.Immutable;
using Romd.Dat.Parsing.Models;

namespace Romd.Dat.Parsing;

public enum DiscGroupReviewReason { SingleObservedDisc, DuplicateDiscNumber, ConflictingTotals, MissingDiscNumbers }

public sealed record DiscGroupCandidate(
    string EditionName,
    string GroupingKey,
    ImmutableArray<ParsedDiscName> Members,
    int? DeclaredDiscCount,
    ImmutableArray<int> MissingDiscNumbers,
    ImmutableArray<DiscGroupReviewReason> ReviewReasons)
{
    public bool IsNameCandidate => ReviewReasons.IsEmpty;
    // Describes names present in this source only, never payload availability.
    public bool HasAllDeclaredDiscNames => DeclaredDiscCount.HasValue && MissingDiscNumbers.IsEmpty
        && !ReviewReasons.Contains(DiscGroupReviewReason.DuplicateDiscNumber)
        && !ReviewReasons.Contains(DiscGroupReviewReason.ConflictingTotals);
}

public sealed record DiscGroupingPreviewResult(
    string RuleVersion,
    string SourceScope,
    int EntriesExamined,
    int WithoutDiscMarker,
    ImmutableArray<ParsedDiscName> ReviewEntries,
    ImmutableArray<DiscGroupCandidate> Groups);

/// <summary>
///     Read-only name candidates within ONE source/platform snapshot. Catalog title assignments,
///     content equivalence, authorization and manual corrections belong to later catalog work.
/// </summary>
public static class DiscGroupingPreview
{
    public const int MaxEntries = 100_000;

    public static DiscGroupingPreviewResult Create(string sourceScope, IEnumerable<string> names)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceScope);
        ArgumentNullException.ThrowIfNull(names);
        var parsed = new List<ParsedDiscName>();
        int total = 0, withoutMarker = 0;
        foreach (string name in names)
        {
            if (++total > MaxEntries)
                throw new ArgumentException($"A preview is limited to {MaxEntries} entries per source.", nameof(names));
            var observation = DiscNameParser.Parse(name);
            if (observation.Status == DiscNameStatus.NoDiscMarker)
                withoutMarker++;
            else
                parsed.Add(observation);
        }

        var groups = parsed.Where(p => p.Status == DiscNameStatus.Parsed)
            .GroupBy(p => p.GroupingKey!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var members = group.OrderBy(p => p.DiscNumber)
                    .ThenBy(p => p.OriginalName, StringComparer.Ordinal).ToImmutableArray();
                int[] totals = members.Where(p => p.DeclaredDiscCount.HasValue)
                    .Select(p => p.DeclaredDiscCount!.Value).Distinct().ToArray();
                int? declared = totals.Length == 1 ? totals[0] : null;
                int upperBound = Math.Max(declared ?? 0, members.Max(p => p.DiscNumber!.Value));
                var present = members.Select(p => p.DiscNumber!.Value).ToHashSet();
                var missing = Enumerable.Range(1, upperBound).Where(n => !present.Contains(n)).ToImmutableArray();
                var reasons = ImmutableArray.CreateBuilder<DiscGroupReviewReason>();
                if (members.Length == 1)
                    reasons.Add(DiscGroupReviewReason.SingleObservedDisc);
                if (present.Count != members.Length)
                    reasons.Add(DiscGroupReviewReason.DuplicateDiscNumber);
                if (totals.Length > 1 || declared < upperBound)
                    reasons.Add(DiscGroupReviewReason.ConflictingTotals);
                if (!missing.IsEmpty)
                    reasons.Add(DiscGroupReviewReason.MissingDiscNumbers);

                return new DiscGroupCandidate(members.Select(p => p.EditionName!).Min(StringComparer.Ordinal)!,
                    group.Key, members, declared, missing, reasons.ToImmutable());
            }).ToImmutableArray();

        return new(DiscNameParser.RuleVersion, sourceScope, total, withoutMarker,
            parsed.Where(p => p.Status == DiscNameStatus.NeedsReview)
                .OrderBy(p => p.OriginalName, StringComparer.Ordinal).ToImmutableArray(), groups);
    }
}
