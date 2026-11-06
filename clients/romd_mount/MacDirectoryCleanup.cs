using System.Runtime.InteropServices;
using System.Text;

namespace Romd.Mount;

internal sealed record MacSocketDirectoryIdentity(string Directory, int Device, ulong DirectoryInode, ulong SocketInode);

// Call only while holding both leases and with no unresolved mount reservation.
// Walk relative to open directory descriptors; never recursively delete a path.
internal static class MacDirectoryCleanup
{
    private const int DirectoryFlags = 0x00100000 | 0x00000100 | 0x01000000; // DIRECTORY, NOFOLLOW, CLOEXEC
    private const ushort DirectoryMode = 0x4000, FileModeBits = 0x8000;
    private const string Lib = "/usr/lib/libSystem.B.dylib";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static int ClearCache(string stateDirectory, string key, CancellationToken ct,
        int maximumEntries = 100_000, int maximumDepth = 64, Action? beforeRemoval = null)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        if (key.Length != 64 || key.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Invalid cache key.");
        ct.ThrowIfCancellationRequested();
        stateDirectory = PrivateStateDirectory.Ensure(stateDirectory);
        int state = open(stateDirectory, DirectoryFlags);
        if (state < 0) throw Failure();
        try
        {
            var anchor = Stat(state);
            int cache = OpenChild(state, "cache", anchor.Device, allowMissing: true, requirePrivate: true);
            if (cache < 0) return 0;
            try
            {
                int profile = OpenChild(cache, key, anchor.Device, allowMissing: true, requirePrivate: true);
                if (profile < 0) return 0;
                try
                {
                    var rootIdentity = Stat(profile);
                    int entries = 0;
                    var nodes = Inspect(profile, 0);
                    // Complete preflight before deleting anything. Cancellation or
                    // failures during deletion may leave a partially cleared cache.
                    beforeRemoval?.Invoke(); // Deterministic race injection for validation.
                    Match(rootIdentity, StatAt(cache, key));
                    Remove(profile, nodes);
                    return entries;

                    List<Node> Inspect(int fd, int depth)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (depth > maximumDepth) throw new IOException("Cache nesting exceeds the cleanup limit.");
                        var nodes = new List<Node>();
                        foreach (string name in Names(fd))
                        {
                            ct.ThrowIfCancellationRequested();
                            if (++entries > maximumEntries) throw new IOException("Cache entries exceed the cleanup limit.");
                            var metadata = StatAt(fd, name);
                            if (metadata.Device != anchor.Device || metadata.Kind is not (DirectoryMode or FileModeBits))
                                throw new IOException("Cache cleanup refuses links, special files, and filesystem boundaries.");
                            List<Node>? children = null;
                            if (metadata.Kind == DirectoryMode)
                            {
                                int child = OpenChild(fd, name, anchor.Device);
                                try { Match(metadata, Stat(child)); children = Inspect(child, depth + 1); }
                                finally { close(child); }
                            }
                            nodes.Add(new(name, metadata, children));
                        }
                        return nodes;
                    }

                    void Remove(int fd, List<Node> nodes)
                    {
                        foreach (var node in nodes)
                        {
                            ct.ThrowIfCancellationRequested();
                            Match(node.Metadata, StatAt(fd, node.Name));
                            if (node.Children is not null)
                            {
                                int child = OpenChild(fd, node.Name, anchor.Device);
                                try { Match(node.Metadata, Stat(child)); Remove(child, node.Children); }
                                finally { close(child); }
                            }
                            Match(node.Metadata, StatAt(fd, node.Name));
                            if (unlinkat(fd, node.Name, node.Children is null ? 0 : 0x80) != 0) throw Failure();
                        }
                    }
                }
                finally { close(profile); }
            }
            finally { close(cache); }
        }
        finally { close(state); }
    }

    internal static MacSocketDirectoryIdentity CaptureSocketDirectory(string directory)
    {
        string name = SocketDirectoryName(directory);
        int parent = open("/private/tmp", DirectoryFlags);
        if (parent < 0) throw Failure();
        try
        {
            int child = OpenChild(parent, name, Stat(parent).Device, requirePrivate: true);
            try
            {
                var folder = Stat(child);
                var socket = StatAt(child, "ipc");
                if (socket.Kind != 0xc000 || socket.Device != folder.Device)
                    throw new IOException("Broker entry is not a local socket.");
                return new(directory, folder.Device, folder.Inode, socket.Inode);
            }
            finally { close(child); }
        }
        finally { close(parent); }
    }

    // Call only after the recorded broker owner and mount child are stopped.
    internal static void ClearSocketDirectory(MacSocketDirectoryIdentity expected)
    {
        string name = SocketDirectoryName(expected.Directory);
        int parent = open("/private/tmp", DirectoryFlags);
        if (parent < 0) throw Failure();
        try
        {
            int child = OpenChild(parent, name, Stat(parent).Device, allowMissing: true, requirePrivate: true);
            if (child < 0) return;
            try
            {
                var folder = Stat(child);
                if (folder.Device != expected.Device || folder.Inode != expected.DirectoryInode)
                    throw new IOException("Broker directory identity changed; preserved.");
                string[] entries = Names(child).Take(2).ToArray();
                if (entries.Length > 1 || (entries.Length == 1 && entries[0] != "ipc"))
                    throw new IOException("Broker directory contains unrecognized entries; preserved.");
                if (entries.Length == 1)
                {
                    var socket = StatAt(child, "ipc");
                    if (socket.Kind != 0xc000 || socket.Device != expected.Device || socket.Inode != expected.SocketInode)
                        throw new IOException("Broker socket identity changed; preserved.");
                    if (unlinkat(child, "ipc", 0) != 0) throw Failure();
                }
                Match(folder, StatAt(parent, name));
                if (unlinkat(parent, name, 0x80) != 0) throw Failure();
            }
            finally { close(child); }
        }
        finally { close(parent); }
    }

    private static string SocketDirectoryName(string directory)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        if (directory is null) throw new IOException("Missing broker directory path.");
        string name = Path.GetFileName(directory);
        if (Path.GetDirectoryName(directory) != "/private/tmp" || !name.StartsWith("romd-token-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[11..], "N", out _))
            throw new IOException("Invalid broker directory path.");
        return name;
    }

    private sealed record Node(string Name, Metadata Metadata, List<Node>? Children);

    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct Metadata
    {
        [FieldOffset(0)] internal int Device;
        [FieldOffset(4)] internal ushort Mode;
        [FieldOffset(8)] internal ulong Inode;
        internal readonly ushort Kind => (ushort)(Mode & 0xf000);
    }

    private static void Match(Metadata expected, Metadata actual)
    {
        if (expected.Device != actual.Device || expected.Inode != actual.Inode || expected.Kind != actual.Kind)
            throw new IOException("Cache changed during cleanup.");
    }

    private static int OpenChild(int fd, string name, int device, bool allowMissing = false, bool requirePrivate = false)
    {
        int child = openat(fd, name, DirectoryFlags);
        if (child < 0)
        {
            if (allowMissing && Marshal.GetLastPInvokeError() == 2) return -1;
            throw Failure();
        }
        try
        {
            var metadata = Stat(child);
            if (metadata.Device != device) throw new IOException("Cache cleanup refuses filesystem boundaries.");
            if (requirePrivate)
            {
                if ((metadata.Mode & 0x1ff) != 0x1c0) throw new IOException("Cache directory must have mode 0700.");
                PrivateStateDirectory.RejectMacAcl(child);
            }
            return child;
        }
        catch { close(child); throw; }
    }

    private static Metadata Stat(int fd)
    {
        Metadata value;
        int result = RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? fstatIntel(fd, out value) : fstat(fd, out value);
        if (result != 0) throw Failure();
        return value;
    }

    private static Metadata StatAt(int fd, string name)
    {
        Metadata value;
        int result = RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? fstatatIntel(fd, name, out value, 0x20) : fstatat(fd, name, out value, 0x20);
        if (result != 0) throw Failure();
        return value;
    }

    private static IEnumerable<string> Names(int fd)
    {
        int duplicate = dup(fd);
        if (duplicate < 0) throw Failure();
        nint directory = fdopendir(duplicate);
        if (directory == 0) { close(duplicate); throw Failure(); }
        try
        {
            rewinddir(directory);
            while (true)
            {
                nint entry = RuntimeInformation.ProcessArchitecture == Architecture.X64
                    ? readdirIntel(directory) : readdir(directory);
                if (entry == 0)
                {
                    if (Marshal.GetLastPInvokeError() != 0) throw Failure();
                    yield break;
                }
                // Darwin 64-bit-inode dirent: name length at 18, name at 21.
                int length = (ushort)Marshal.ReadInt16(entry, 18);
                int recordLength = (ushort)Marshal.ReadInt16(entry, 16);
                if (length is < 1 or > 1023 || recordLength < 22 + length) throw Failure();
                byte[] bytes = new byte[length];
                Marshal.Copy(entry + 21, bytes, 0, length);
                string name = StrictUtf8.GetString(bytes);
                if (name is "." or "..") continue;
                if (name.Contains('/') || name.Contains('\0')) throw Failure();
                yield return name;
            }
        }
        finally { closedir(directory); }
    }

    private static IOException Failure() => new("Cache cleanup failed; cached files may have been partially removed.");

    [DllImport(Lib, SetLastError = true)] private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport(Lib, SetLastError = true)] private static extern int openat(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport(Lib, SetLastError = true)] private static extern int unlinkat(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport(Lib)] private static extern int close(int fd);
    [DllImport(Lib, SetLastError = true)] private static extern int dup(int fd);
    [DllImport(Lib, SetLastError = true)] private static extern nint fdopendir(int fd);
    [DllImport(Lib)] private static extern void rewinddir(nint directory);
    [DllImport(Lib)] private static extern int closedir(nint directory);
    [DllImport(Lib, SetLastError = true)] private static extern nint readdir(nint directory);
    [DllImport(Lib, EntryPoint = "readdir$INODE64", SetLastError = true)] private static extern nint readdirIntel(nint directory);
    [DllImport(Lib, SetLastError = true)] private static extern int fstat(int fd, out Metadata metadata);
    [DllImport(Lib, EntryPoint = "fstat$INODE64", SetLastError = true)] private static extern int fstatIntel(int fd, out Metadata metadata);
    [DllImport(Lib, SetLastError = true)] private static extern int fstatat(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, out Metadata metadata, int flags);
    [DllImport(Lib, EntryPoint = "fstatat$INODE64", SetLastError = true)] private static extern int fstatatIntel(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, out Metadata metadata, int flags);
}
