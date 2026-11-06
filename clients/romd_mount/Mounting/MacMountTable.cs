using System.Runtime.InteropServices;
using System.Text;

namespace Romd.Mount.Mounting;

internal sealed record MacMountIdentity(int Id0, int Id1, uint Owner, string FileSystem, string Source, bool ReadOnly);

internal static class MacMountTable
{
    private const string Lib = "/usr/lib/libSystem.B.dylib";
    private const int RecordSize = 2168; // Darwin 64-bit-inode statfs, sys/mount.h.

    internal static Guid BootSession()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        byte[] bytes = new byte[128];
        nuint size = (nuint)bytes.Length;
        if (sysctlbyname("kern.bootsessionuuid", bytes, ref size, 0, 0) != 0 || size is 0 or > 128)
            throw new IOException("Cannot determine the macOS boot session.");
        string text = Encoding.ASCII.GetString(bytes, 0, (int)size).TrimEnd('\0');
        if (!Guid.TryParseExact(text, "D", out Guid boot)) throw new IOException("Invalid macOS boot session.");
        return boot;
    }

    internal static MacMountIdentity? Find(string mountpoint)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        // MNT_NOWAIT uses the kernel's cached mount table: do not stat a potentially
        // unreachable NFS path or wait for remote filesystem statistics.
        int count = Get(0, 0);
        if (count is < 0 or > 4096) throw new IOException("Cannot inspect the mount table.");
        int capacity = Math.Min(count + 16, 4096);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            nint buffer = Marshal.AllocHGlobal(capacity * RecordSize);
            try
            {
                count = Get(buffer, capacity * RecordSize);
                if (count < 0) throw new IOException("Cannot inspect the mount table.");
                if (count >= capacity) { capacity = Math.Min(capacity * 2, 4096); continue; }
                MacMountIdentity? found = null;
                for (int i = 0; i < count; i++)
                {
                    nint record = buffer + i * RecordSize;
                    if (Text(record + 88, 1024) != mountpoint) continue;
                    if (found is not null) throw new IOException("Ambiguous mountpoint identity.");
                    found = new(Marshal.ReadInt32(record, 48), Marshal.ReadInt32(record, 52),
                        unchecked((uint)Marshal.ReadInt32(record, 56)), Text(record + 72, 16),
                        Text(record + 1112, 1024), (Marshal.ReadInt32(record, 64) & 1) != 0);
                }
                return found;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new IOException("Mount table changed or exceeds the inspection limit.");
    }

    private static int Get(nint buffer, int bytes) => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => getfsstat(buffer, bytes, 2),
        Architecture.X64 => getfsstatIntel(buffer, bytes, 2),
        _ => throw new PlatformNotSupportedException()
    };

    private static string Text(nint pointer, int size)
    {
        byte[] bytes = new byte[size];
        Marshal.Copy(pointer, bytes, 0, size);
        int length = Array.IndexOf(bytes, (byte)0);
        if (length < 0) throw new IOException("Invalid mount table string.");
        return new UTF8Encoding(false, true).GetString(bytes, 0, length);
    }

    [DllImport(Lib, SetLastError = true)] private static extern int getfsstat(nint buffer, int size, int flags);
    [DllImport(Lib, EntryPoint = "getfsstat$INODE64", SetLastError = true)] private static extern int getfsstatIntel(nint buffer, int size, int flags);
    [DllImport(Lib, SetLastError = true)] private static extern int sysctlbyname(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[] value, ref nuint size, nint replacement, nuint replacementSize);
}
