namespace Romd.Mount.Mounting;

/// <summary>Per-mount eviction targets. Neither setting is a strict disk quota.</summary>
public sealed class MountCacheOptions
{
    public static MountCacheOptions Default { get; } = new(1024, 0);

    public MountCacheOptions(int sizeMiB, int minimumFreeMiB)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sizeMiB, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumFreeMiB);
        SizeMiB = sizeMiB;
        MinimumFreeMiB = minimumFreeMiB;
    }

    public int SizeMiB { get; }
    public int MinimumFreeMiB { get; }
}
