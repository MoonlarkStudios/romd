using System.Runtime.InteropServices;

namespace Romd.Mount;

/// <summary>Rejects permissive or redirected state instead of silently reusing it.</summary>
internal static class PrivateStateDirectory
{
    internal const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static string Ensure(string path)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Private Unix state requires a Unix filesystem.");
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("State directory must be absolute.");
        path = Path.GetFullPath(path);
        RejectLink(path);
        Directory.CreateDirectory(path, OwnerOnly);
        return Verify(path);
    }

    /// <summary>Applies the same checks as <see cref="Ensure"/> without creating anything.</summary>
    public static string Verify(string path)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Private Unix state requires a Unix filesystem.");
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("State directory must be absolute.");
        path = Path.GetFullPath(path);
        RejectLink(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Private directory does not exist.");
        const UnixFileMode access = OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((File.GetUnixFileMode(path) & access) != OwnerOnly)
            throw new IOException("State directory must grant access only to its owner (mode 0700).");
        if (OperatingSystem.IsMacOS()) RejectMacAcl(path);
        return path;
    }

    private static void RejectMacAcl(string path)
    {
        // macOS ACLs can grant access independently of POSIX mode bits. Do not
        // rewrite an existing ACL or try to infer which entries are harmless.
        // O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC, Darwin sys/fcntl.h.
        int fd = open(path, 0x00100000 | 0x00000100 | 0x01000000);
        if (fd < 0) throw new IOException("Cannot open state directory for ACL verification.");
        try { RejectMacAcl(fd); }
        finally { close(fd); }
    }

    internal static void RejectMacAcl(int fd)
    {
        nint acl = acl_get_fd_np(fd, 0x100); // ACL_TYPE_EXTENDED
        if (acl == 0)
        {
            // Darwin returns ENOENT when this valid open directory has no ACL.
            if (Marshal.GetLastPInvokeError() == 2) return;
            throw new IOException("Cannot verify state directory ACL.");
        }
        try
        {
            if (acl_valid(acl) != 0) throw new IOException("Cannot validate state directory ACL.");
            int result = acl_get_entry(acl, 0, out _); // ACL_FIRST_ENTRY
            int error = Marshal.GetLastPInvokeError();
            if (result == 0) throw new IOException("State directory must not have an extended ACL.");
            if (result != -1 || error != 22) throw new IOException("Cannot inspect state directory ACL.");
        }
        finally { acl_free(acl); }
    }

    [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern int close(int fd);
    [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
    private static extern nint acl_get_fd_np(int fd, int type);
    [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
    private static extern int acl_valid(nint acl);
    [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
    private static extern int acl_get_entry(nint acl, int entryId, out nint entry);
    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern int acl_free(nint acl);

    private static void RejectLink(string path)
    {
        if (new DirectoryInfo(path).LinkTarget is not null)
            throw new IOException("State directory must not be a symbolic link.");
    }
}
