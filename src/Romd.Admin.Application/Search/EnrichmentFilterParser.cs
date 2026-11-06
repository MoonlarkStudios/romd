using System.Collections.Frozen;
using ErrorOr;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Search;

/// <summary>
///     Parses the enrichment filter vocabulary shared by catalog search and completion scopes.
///     Names match exactly; numeric, case-folded, and comma-combined enum forms are rejected.
/// </summary>
public static class EnrichmentFilterParser
{
    private static readonly FrozenDictionary<string, EnrichmentFlagFilter> Flags =
        Enum.GetValues<EnrichmentFlagFilter>().ToFrozenDictionary(flag => flag.ToString(), StringComparer.Ordinal);

    private static readonly FrozenSet<string> LastAttemptStatuses =
        Enum.GetNames<EnrichmentStatus>().ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    ///     Returns the distinct flags in chip order, or every validation failure: unknown values and
    ///     Settled combined with a flag.
    /// </summary>
    public static ErrorOr<EnrichmentFlagFilter[]> ParseFlags(IReadOnlyCollection<string?> values)
    {
        if (values.Count == 0)
            return SearchErrors.EnrichmentFilterRequired;
        var errors = values.Where(value => value is null || !Flags.ContainsKey(value)).Distinct()
            .Select(SearchErrors.InvalidEnrichmentFilter).ToList();
        var flags = values.OfType<string>().Where(Flags.ContainsKey).Select(value => Flags[value]).Distinct().Order().ToArray();
        if (flags.Contains(EnrichmentFlagFilter.Settled) && flags.Length > 1)
            errors.Add(SearchErrors.EnrichmentFilterConflict);
        return errors.Count > 0 ? errors : flags;
    }

    /// <summary>
    ///     True for a last enrichment attempt status name (<see cref="EnrichmentStatus"/>).
    /// </summary>
    public static bool IsLastAttemptStatus(string value) => LastAttemptStatuses.Contains(value);
}
