using System.Runtime.InteropServices;

namespace Romd.Mount.Cli;

internal static class MacFileAllocation
{
    // Darwin's 64-bit-inode struct stat, from sys/stat.h. Both supported macOS
    // architectures use this layout; Intel selects the INODE64 symbol explicitly.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct DarwinStat
    {
        [FieldOffset(4)] internal ushort Mode;
        [FieldOffset(96)] internal long Size;
        [FieldOffset(104)] internal long Blocks;
    }

    internal static (long Logical, long Allocated) Read(string path)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        DarwinStat stat;
        int result = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => LStat(path, out stat),
            Architecture.X64 => LStatIntel(path, out stat),
            _ => throw new PlatformNotSupportedException()
        };
        if (result != 0)
            throw new IOException("Cannot inspect cache file allocation.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        // lstat never follows the final link. Refuse sockets, FIFOs, devices and
        // links rather than treating unsupported entries as empty regular files.
        if ((stat.Mode & 0xf000) != 0x8000 || stat.Size < 0 || stat.Blocks < 0)
            throw new IOException("Cache entry must be an ordinary file.");
        return (stat.Size, checked(stat.Blocks * 512));
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "lstat", SetLastError = true)]
    private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out DarwinStat stat);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "lstat$INODE64", SetLastError = true)]
    private static extern int LStatIntel([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out DarwinStat stat);
}
