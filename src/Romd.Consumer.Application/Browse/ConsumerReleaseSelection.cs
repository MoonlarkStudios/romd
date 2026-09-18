using Romd.Consumer.Application.Browse.ReadModels;

namespace Romd.Consumer.Application.Browse;

public sealed record ConsumerReleaseSelectionCandidate(
    ConsumerReleaseData Release,
    bool IsPlayable,
    IReadOnlyList<int> RegionIds,
    IReadOnlyList<int> LanguageIds);

public interface IConsumerReleaseSelector
{
    ConsumerReleaseData? SelectDefault(
        IReadOnlyList<ConsumerReleaseSelectionCandidate> releases,
        ConsumerReleasePreference preference);
}

public sealed class ConsumerReleaseSelector : IConsumerReleaseSelector
{
    public ConsumerReleaseData? SelectDefault(
        IReadOnlyList<ConsumerReleaseSelectionCandidate> releases,
        ConsumerReleasePreference preference)
    {
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(preference);

        var ordered = releases
            .OrderBy(candidate => GetBestRank(candidate.RegionIds, preference.PreferredRegionIds))
            .ThenBy(candidate => GetBestRank(candidate.LanguageIds, preference.PreferredLanguageIds));
        ordered = ApplyRevisionPreference(ordered, preference.RevisionStrategy);

        return ordered
            .ThenByDescending(candidate => candidate.IsPlayable)
            .ThenByDescending(candidate => candidate.Release.IsComplete)
            .ThenByDescending(candidate => ParseRevisionValue(candidate.Release.Revision))
            .ThenBy(candidate => candidate.Release.Name)
            .ThenBy(candidate => candidate.Release.Id)
            .Select(candidate => candidate.Release)
            .FirstOrDefault();
    }

    private static int GetBestRank(
        IReadOnlyList<int> releaseIds,
        IReadOnlyList<int> preferredIds)
    {
        if (preferredIds.Count == 0)
        {
            return 0;
        }

        var releaseIdSet = releaseIds.ToHashSet();
        for (int i = 0; i < preferredIds.Count; i++)
        {
            if (releaseIdSet.Contains(preferredIds[i]))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static IOrderedEnumerable<ConsumerReleaseSelectionCandidate> ApplyRevisionPreference(
        IOrderedEnumerable<ConsumerReleaseSelectionCandidate> ordered,
        ConsumerRevisionPreference strategy) =>
        strategy switch
        {
            ConsumerRevisionPreference.NewestFirst => ordered.ThenByDescending(candidate =>
                ParseRevisionValue(candidate.Release.Revision)),
            ConsumerRevisionPreference.OldestFirst => ordered.ThenBy(candidate =>
                ParseRevisionValue(candidate.Release.Revision)),
            _ => ordered.ThenBy(_ => 0)
        };

    private static RevisionSortKey ParseRevisionValue(string? revision)
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
                if (segments[segmentIndex] > (int.MaxValue - digit) / 10)
                {
                    segments[segmentIndex] = int.MaxValue;
                    continue;
                }

                segments[segmentIndex] = (segments[segmentIndex] * 10) + digit;
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
