namespace Romd.Domain.Catalog;

/// <summary>
///     Global tracked-collection preference policy. A per-title pin wins; otherwise releases
///     prefer verified dumps, then rank by configured region, newest parsed revision, and stable
///     name/id tie-breakers. Satisfaction is intentionally outside this policy.
/// </summary>
public sealed class CatalogReleasePreferencePolicy
{
    private readonly IReadOnlyList<string> _regionPriority;

    public CatalogReleasePreferencePolicy(IEnumerable<string> regionPriority)
    {
        ArgumentNullException.ThrowIfNull(regionPriority);
        _regionPriority = regionPriority.ToList();
    }

    public CatalogReleasePreferenceCandidate? SelectDesired(
        IEnumerable<CatalogReleasePreferenceCandidate> releases,
        int? pinnedCatalogReleaseId = null)
    {
        ArgumentNullException.ThrowIfNull(releases);
        var materialized = releases as IReadOnlyList<CatalogReleasePreferenceCandidate> ?? releases.ToList();

        if (pinnedCatalogReleaseId is int pinnedId)
        {
            return materialized.SingleOrDefault(release => release.CatalogReleaseId == pinnedId);
        }

        var preferredQuality = materialized.Any(release => !release.HasInferiorDump)
            ? materialized.Where(release => !release.HasInferiorDump)
            : materialized;

        return Order(preferredQuality).FirstOrDefault();
    }

    public IOrderedEnumerable<CatalogReleasePreferenceCandidate> Order(
        IEnumerable<CatalogReleasePreferenceCandidate> releases)
    {
        ArgumentNullException.ThrowIfNull(releases);
        return releases
            .OrderBy(release => RegionRank(release.Region))
            .ThenByDescending(release => ParseRevision(release.Revision))
            .ThenBy(release => release.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(release => release.CatalogReleaseId);
    }

    public static bool IsInferiorDumpStatus(string? status) =>
        status?.Equals("baddump", StringComparison.OrdinalIgnoreCase) == true
        || status?.Equals("nodump", StringComparison.OrdinalIgnoreCase) == true;

    private int RegionRank(string? region)
    {
        if (string.IsNullOrWhiteSpace(region))
        {
            return int.MaxValue;
        }

        for (int index = 0; index < _regionPriority.Count; index++)
        {
            if (_regionPriority[index].Equals(region, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    private static RevisionSortKey ParseRevision(string? revision)
    {
        if (string.IsNullOrWhiteSpace(revision))
        {
            return default;
        }

        Span<int> segments = stackalloc int[4];
        int segmentIndex = 0;
        bool readingNumber = false;
        foreach (char character in revision)
        {
            if (char.IsDigit(character))
            {
                readingNumber = true;
                int digit = character - '0';
                segments[segmentIndex] = segments[segmentIndex] > (int.MaxValue - digit) / 10
                    ? int.MaxValue
                    : (segments[segmentIndex] * 10) + digit;
                continue;
            }

            if (!readingNumber)
            {
                continue;
            }

            if (segmentIndex == segments.Length - 1)
            {
                break;
            }

            segmentIndex++;
            readingNumber = false;
        }

        return new RevisionSortKey(segments[0], segments[1], segments[2], segments[3]);
    }

    private readonly record struct RevisionSortKey(int Major, int Minor, int Patch, int Build)
        : IComparable<RevisionSortKey>
    {
        public int CompareTo(RevisionSortKey other)
        {
            int major = Major.CompareTo(other.Major);
            if (major != 0) return major;

            int minor = Minor.CompareTo(other.Minor);
            if (minor != 0) return minor;

            int patch = Patch.CompareTo(other.Patch);
            return patch != 0 ? patch : Build.CompareTo(other.Build);
        }
    }
}

public sealed record CatalogReleasePreferenceCandidate(
    int CatalogReleaseId,
    string Name,
    string? Region,
    string? Revision,
    bool HasInferiorDump);
