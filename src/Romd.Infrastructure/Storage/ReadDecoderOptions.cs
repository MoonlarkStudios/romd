namespace Romd.Infrastructure.Storage;

public sealed class ReadDecoderOptions
{
    public int MaxConcurrent { get; set; } = 4;
    /// <summary>Maximum queued artifact snapshots awaiting a decoded-reader slot; zero disables waiting.</summary>
    public int MaxQueued { get; set; } = 8;
    /// <summary>Default bounded admission wait; an explicit caller timeout overrides it and zero is fail-fast.</summary>
    public int AdmissionTimeoutMilliseconds { get; set; } = 5000;
    public int MaxIdle { get; set; } = 2;
    /// <summary>Byte budget for decoded media blocks shared by every reader in the host.</summary>
    public long CacheBytes { get; set; } = 64L << 20;
    internal void Validate()
    {
        if (MaxConcurrent is < 1 or > 16 || MaxQueued is < 0 or > 128 ||
            AdmissionTimeoutMilliseconds is < 0 or > 300000 || MaxIdle < 0 || MaxIdle > MaxConcurrent || CacheBytes is < 0 or > 4L << 30)
            throw new InvalidOperationException("Invalid Romd:MediaRead decoder limits.");
    }
}
