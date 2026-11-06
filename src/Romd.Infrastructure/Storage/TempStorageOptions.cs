namespace Romd.Infrastructure.Storage;

/// <summary>Shared staging limits for every host using the same data directory.</summary>
public sealed class TempStorageOptions
{
    public int MaximumFiles { get; set; } = 64;
    public long MaximumBytes { get; set; } = 128L << 30;
    public long MinimumFreeBytes { get; set; } = 1L << 30;
    public int AdmissionTimeoutSeconds { get; set; } = 5;
    public int CopyTimeoutSeconds { get; set; } = 3600;

    internal TempStorageOptions ValidatedCopy(long maximumFileBytes)
    {
        if (MaximumFiles is < 1 or > 256 || MaximumBytes < maximumFileBytes ||
            MaximumBytes > (16L << 40) || MinimumFreeBytes is < 0 or > (16L << 40) ||
            AdmissionTimeoutSeconds is < 1 or > 60 || CopyTimeoutSeconds is < 1 or > 86400)
            throw new InvalidOperationException("Invalid Romd:TempStorage limits.");
        return (TempStorageOptions)MemberwiseClone();
    }
}
