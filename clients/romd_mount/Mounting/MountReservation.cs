using System.Diagnostics;
using System.Text.Json;
using Romd.Mount.Credentials;

namespace Romd.Mount.Mounting;

/// <summary>
/// Process-crash fence, used while holding the shared credential lease. It is
/// deliberately not IDisposable: losing the owner must preserve the reservation.
/// </summary>
internal sealed class MountReservation
{
    private readonly string _path;
    private Record _record;

    internal static string DirectoryPath => Path.Combine(MountProfileLease.CredentialLockDirectory, "mounts");

    internal static void EnsureAbsent(string directory, string key)
    {
        string path = RecordPath(directory, key);
        if (File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null)
            throw new MountPrerequisiteException("An earlier mount has an unresolved reservation at " + path +
                ". Preserve its cache and verify the old mount and process are stopped before recovery.");
    }

    internal static MountReservation Create(string directory, string key, string mountpoint, string backend, string executable,
        MacSocketDirectoryIdentity? brokerDirectory = null, bool launchGated = false)
    {
        EnsureAbsent(directory, key);
        string path = RecordPath(directory, key);
        using var owner = Process.GetCurrentProcess();
        var record = new Record(2, mountpoint, backend, Path.GetFullPath(executable), Environment.ProcessId,
            owner.StartTime.ToUniversalTime(), null, null,
            OperatingSystem.IsMacOS() ? MacMountTable.BootSession() : null, null, brokerDirectory, launchGated);
        // CreateNew provides a second fence if a cooperating caller loses its lease.
        Write(path, record);
        return new MountReservation(path, record);
    }

    private MountReservation(string path, Record record) { _path = path; _record = record; }

    internal Record State => _record;

    internal static MountReservation? Read(string directory, string key)
    {
        string path = RecordPath(directory, key);
        if (new FileInfo(path).LinkTarget is not null || Directory.Exists(path))
            throw new IOException("Invalid mount reservation.");
        if (!File.Exists(path)) return null;
        if (OperatingSystem.IsMacOS()) _ = Romd.Mount.Cli.MacFileAllocation.Read(path);
        using var stream = File.OpenRead(path);
        byte[] bytes = new byte[16_385];
        int count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (count > 16_384) throw new IOException("Mount reservation exceeds its size limit.");
        var record = JsonSerializer.Deserialize<Record>(bytes.AsSpan(0, count), new JsonSerializerOptions
        { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow });
        if (record is null || record.Version != 2 || record.BootSession is null || record.BootSession == Guid.Empty ||
            !ValidPath(record.Mountpoint) || !ValidPath(record.Executable) || !ValidBroker(record.BrokerDirectory) ||
            record.Backend is not ("nfs-development" or "macfuse-fskit") || record.OwnerPid < 1 ||
            record.OwnerStartedUtc.Kind != DateTimeKind.Utc || record.OwnerStartedUtc == default ||
            (record.ChildPid.HasValue != record.ChildStartedUtc.HasValue) ||
            (record.ChildPid.HasValue && (record.ChildPid < 1 || record.ChildStartedUtc!.Value.Kind != DateTimeKind.Utc || record.ChildStartedUtc.Value == default)))
            throw new IOException("Reservation lacks valid version-2 recovery evidence.");
        return new(path, record);
    }

    internal void ClearRecovered(string directory, string key)
    {
        if (Read(directory, key)?.State != _record) throw new IOException("Mount reservation changed during recovery.");
        File.Delete(_path);
    }

    private static bool ValidPath(string? path) => path is not null && path.Length <= 4096 &&
        Path.IsPathFullyQualified(path) && path != "/" && !path.Any(char.IsControl) && Path.GetFullPath(path) == path;

    private static bool ValidBroker(MacSocketDirectoryIdentity? broker)
    {
        if (broker is null) return true; // Older version-2 records have no IPC evidence.
        const string prefix = "/private/tmp/romd-token-";
        return broker.Directory is { } path && path.StartsWith(prefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(path[prefix.Length..], "N", out _) && broker.DirectoryInode != 0 && broker.SocketInode != 0;
    }

    internal void RecordChild(Process child)
        => Replace(_record with { ChildPid = child.Id, ChildStartedUtc = child.StartTime.ToUniversalTime() });

    internal void RecordMounted()
    {
        var mounted = MacMountTable.Find(_record.Mountpoint) ?? throw new IOException("Mount disappeared before identity capture.");
        if (!IsExpectedIdentity(_record.Backend, mounted))
            throw new IOException("Unexpected mounted filesystem identity.");
        Replace(_record with { MountIdentity = mounted });
    }

    // FSKit volumes never report MNT_RDONLY even when rclone enforces read-only
    // access, so the supervisor proves that backend read-only with a write probe.
    internal static bool IsExpectedIdentity(string backend, MacMountIdentity mounted) => backend switch
    {
        "nfs-development" => mounted is { ReadOnly: true, FileSystem: "nfs" },
        "macfuse-fskit" => mounted.FileSystem == "macfuse",
        _ => false
    };

    private void Replace(Record updated)
    {
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Write(temporary, updated);
            File.Move(temporary, _path, overwrite: true);
            _record = updated;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // Only the owning supervisor calls this after proven unmount and child exit,
    // or after a launch failure that guarantees no mount child was created.
    internal void ClearAfterStop() => File.Delete(_path);

    private static string RecordPath(string directory, string key)
    {
        if (key.Length != 64 || key.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Invalid reservation key.");
        return Path.Combine(PrivateStateDirectory.Ensure(directory), key + ".json");
    }

    private static void Write(string path, Record record)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        JsonSerializer.Serialize(stream, record);
        stream.Flush(flushToDisk: true);
    }

    internal sealed record Record(int Version, string Mountpoint, string Backend, string Executable,
        int OwnerPid, DateTime OwnerStartedUtc, int? ChildPid, DateTime? ChildStartedUtc,
        Guid? BootSession, MacMountIdentity? MountIdentity, MacSocketDirectoryIdentity? BrokerDirectory = null,
        bool LaunchGated = false);
}
