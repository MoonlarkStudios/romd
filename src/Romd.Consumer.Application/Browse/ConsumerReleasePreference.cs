namespace Romd.Consumer.Application.Browse;

public enum ConsumerRevisionPreference
{
    None = 0,
    NewestFirst = 1,
    OldestFirst = 2
}

public sealed record ConsumerReleasePreference(
    IReadOnlyList<int> PreferredRegionIds,
    IReadOnlyList<int> PreferredLanguageIds,
    ConsumerRevisionPreference RevisionStrategy)
{
    public static ConsumerReleasePreference Default { get; } = new([], [], ConsumerRevisionPreference.None);
}
