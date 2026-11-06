using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Romd.Media.Processes;

/// <summary>Close-only flock ownership. A dup inherited by a child shares this open file description.</summary>
internal sealed class NativeFileLease : SafeHandle
{
    private NativeFileLease(int descriptor) : base(new nint(-1), ownsHandle: true) => SetHandle(descriptor);
    public override bool IsInvalid => handle == new nint(-1);
    internal int Descriptor => checked((int)handle);
    internal MediaAdmissionIdentity Identity
    {
        get
        {
            byte[] stat = new byte[512];
            if ((OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? FstatDarwin64(Descriptor, stat) : Fstat(Descriptor, stat)) != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            // Linux glibc x64/arm64 stat starts with 64-bit dev/ino. Darwin's
            // dev_t is 32-bit, followed by alignment and its 64-bit inode.
            ulong device = OperatingSystem.IsMacOS() ? BitConverter.ToUInt32(stat, 0) : BitConverter.ToUInt64(stat, 0);
            ulong inode = BitConverter.ToUInt64(stat, 8);
            if (inode == 0)
                throw new InvalidDataException("The admission file identity is invalid.");
            return new(OperatingSystem.IsMacOS() ? "darwin" : "linux", device, inode);
        }
    }

    internal static NativeFileLease? TryAcquire(string path)
    {
        // Set close-on-exec atomically with open: a later fcntl races concurrent
        // fork/exec and can let an unrelated child retain this flock after parent close.
        int flags = 2 | (OperatingSystem.IsMacOS() ? 0x200 | 0x1000000 : 0x40 | 0x80000);
        int descriptor = Open(path, flags, 0x180);
        if (descriptor < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            if (Flock(descriptor, 2 | 4) == 0)
            {
                var result = new NativeFileLease(descriptor);
                descriptor = -1;
                return result;
            }
            int error = Marshal.GetLastPInvokeError();
            if (error is 11 or 35)
                return null;
            throw new Win32Exception(error);
        }
        finally { if (descriptor >= 0) Close(descriptor); }
    }

    // Held descriptor kind, not a pathname/seekability inference. Parent path custody is caller-owned.
    internal static SafeFileHandle OpenRegularRead(string path)
    {
        bool mac = OperatingSystem.IsMacOS();
        Architecture architecture = RuntimeInformation.ProcessArchitecture;
        if ((!mac && !OperatingSystem.IsLinux()) || architecture is not (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException("Regular file custody uses the supported POSIX ABI.");
        // Read-only, nonblocking, leaf no-follow and close-on-exec. Nonblocking avoids waiting on a FIFO open.
        int flags = mac ? 0x4 | 0x100 | 0x1000000
            : 0x800 | (architecture == Architecture.Arm64 ? 0x8000 : 0x20000) | 0x80000;
        int descriptor = Open(path, flags, 0);
        if (descriptor < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == (mac ? 62 : 40))
                throw new InvalidDataException("The input is a leaf symlink.");
            throw new IOException("The source descriptor could not be opened.", new Win32Exception(error));
        }
        try
        {
            byte[] stat = new byte[512];
            if ((mac && architecture == Architecture.X64 ? FstatDarwin64(descriptor, stat) : Fstat(descriptor, stat)) != 0)
                throw new IOException("The held descriptor kind could not be measured.", new Win32Exception(Marshal.GetLastPInvokeError()));
            uint mode = mac ? BitConverter.ToUInt16(stat, 4)
                : BitConverter.ToUInt32(stat, architecture == Architecture.Arm64 ? 16 : 24);
            if ((mode & 0xf000) != 0x8000)
                throw new InvalidDataException("The held input is not a regular file.");
            var owner = new SafeFileHandle(descriptor, ownsHandle: true);
            descriptor = -1;
            return owner;
        }
        finally { if (descriptor >= 0) Close(descriptor); }
    }

    internal void Bind(string nonce)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(nonce);
        if (bytes.Length != 32 || Ftruncate(Descriptor, 0) != 0 || Pwrite(Descriptor, bytes, (nuint)bytes.Length, 0) != bytes.Length ||
            Fsync(Descriptor) != 0)
            throw new IOException("The native admission lease identity could not be persisted.");
    }

    // Never call LOCK_UN: it would release the child's shared open-file-description lock too.
    protected override bool ReleaseHandle() => Close(checked((int)handle)) == 0;

    private static bool DarwinArm64 => OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
    private static int Open(string path, int flags, int mode) => DarwinArm64
        ? OpenDarwinArm64(path, flags, 0, 0, 0, 0, 0, 0, mode) : OpenNative(path, flags, mode);
    internal static int Fcntl(int descriptor, int command, int argument) => DarwinArm64
        ? FcntlDarwinArm64(descriptor, command, 0, 0, 0, 0, 0, 0, argument) : FcntlNative(descriptor, command, argument);

    // Darwin arm64 places optional C arguments on the stack. Six inert integer
    // slots occupy x2..x7 so the ninth fixed argument occupies that stack slot.
    // Other supported ABIs pass the optional integer through the normal register.
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int OpenNative([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenDarwinArm64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags,
        nint x2, nint x3, nint x4, nint x5, nint x6, nint x7, nint mode);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)] internal static extern int Close(int descriptor);
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)] private static extern int FcntlNative(int descriptor, int command, int argument);
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int FcntlDarwinArm64(int descriptor, int command,
        nint x2, nint x3, nint x4, nint x5, nint x6, nint x7, nint argument);
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)] private static extern int Flock(int descriptor, int operation);
    [DllImport("libc", EntryPoint = "ftruncate", SetLastError = true)] private static extern int Ftruncate(int descriptor, long length);
    [DllImport("libc", EntryPoint = "pwrite", SetLastError = true)] private static extern nint Pwrite(int descriptor, byte[] bytes, nuint count, long offset);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)] private static extern int Fsync(int descriptor);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)] private static extern int Fstat(int descriptor, [Out] byte[] stat);
    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)] private static extern int FstatDarwin64(int descriptor, [Out] byte[] stat);
}
