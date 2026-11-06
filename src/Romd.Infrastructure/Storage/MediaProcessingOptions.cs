namespace Romd.Infrastructure.Storage;

/// <summary>Per-host admission and per-file limits for verifying untrusted uploaded media (Romd:Media).</summary>
public sealed class MediaProcessingOptions
{
    public int MaxConcurrent { get; set; } = 2;
    public int AdmissionTimeoutSeconds { get; set; } = 5;
    public int WallTimeoutSeconds { get; set; } = 600;
    public long MaxArtifactBytes { get; set; } = 10L << 30;
    public long MaxLogicalBytes { get; set; } = 10L << 30;
    public string WorkerPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "media-worker", "Romd.Media.Worker.dll");
    public string DotnetPath { get; set; } = Path.GetFullPath(Path.Combine(
        System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
    public long MemoryBytes { get; set; } = 512L << 20;
    public long LinuxAddressSpaceBytes { get; set; } = 32L << 30;
    public long LinuxDataBytes { get; set; } = 1L << 30;
    public int CpuTimeoutSeconds { get; set; } = 600;
    public int ChdEncodeCpuTimeoutSeconds { get; set; } = 1200;
    public int ChdEncodeWallTimeoutSeconds { get; set; } = 1200;
    internal MediaProcessingOptions ValidatedCopy()
    {
        if (ChdEncodeCpuTimeoutSeconds is < 1 or > 14400 || ChdEncodeWallTimeoutSeconds is < 1 or > 14400 ||
            MaxConcurrent is < 1 or > 16 || AdmissionTimeoutSeconds is < 1 or > 60 || WallTimeoutSeconds is < 1 or > 14400 ||
            MaxArtifactBytes is < 4 or > (16L << 30) || MaxLogicalBytes is < 0x81 or > (16L << 30) ||
            MemoryBytes is < (64L << 20) or > (8L << 30) || LinuxDataBytes < MemoryBytes || LinuxDataBytes > (16L << 30) ||
            LinuxAddressSpaceBytes < LinuxDataBytes || LinuxAddressSpaceBytes > (64L << 30) || CpuTimeoutSeconds is < 1 or > 14400 ||
            !Path.IsPathFullyQualified(WorkerPath) || !Path.IsPathFullyQualified(DotnetPath))
            throw new InvalidOperationException("Invalid Romd:Media processing limits.");
        return (MediaProcessingOptions)MemberwiseClone();
    }
}
