using Romd.Media.Inspection;

namespace Romd.Infrastructure.Storage;

/// <summary>Observation-shape checks after the trusted supervisor authenticates the packaged worker and stopped process tree.</summary>
internal static class MediaWorkerObservationIdentity
{
    internal static bool Matches(MediaWorkerIdentity? identity, MediaWorkerExecution? execution, MediaProcessingOptions options)
    {
        const string workerPrefix = "romd-media-worker/mvid:";
        return identity is not null && execution is not null && execution.ProcessId > 0 && execution.ExitCode == 0 &&
            execution.ProcessId == identity.ProcessId && execution.StandardOutputBytes is > 0 and <= MediaWorkerProtocol.MaximumOutputBytes &&
            execution.StandardErrorBytes is >= 0 and <= 16384 &&
            (OperatingSystem.IsWindows() || identity.SessionId == identity.ProcessId) &&
            identity.DecoderBuild == MediaInspectionService.DecoderBuild && identity.WorkerBuild is { } worker &&
            worker.StartsWith(workerPrefix, StringComparison.Ordinal) && Guid.TryParseExact(worker[workerPrefix.Length..], "D", out var mvid) && mvid != Guid.Empty &&
            identity.GcHeapLimitBytes == options.MemoryBytes && identity.GcAvailableBytes > 0 && identity.GcAvailableBytes <= options.MemoryBytes &&
            identity.GcRegionRangeBytes >= 0 && (OperatingSystem.IsLinux()
                ? identity.LinuxLimits == new MediaWorkerLimits(options.LinuxAddressSpaceBytes, options.LinuxDataBytes, options.CpuTimeoutSeconds, 0)
                : identity.LinuxLimits is null);
    }

}
