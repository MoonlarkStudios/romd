using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Romd.Media.Processes;

/// <summary>
///     An executable image copied into a sealed Linux memfd. Its size and SHA-256 are measured from the sealed
///     bytes, and executing its descriptor runs exactly those bytes: identity is established before launch,
///     never by racing the started process.
/// </summary>
internal sealed class SealedExecutable : IDisposable
{
    internal const long MaximumBytes = 64L << 20;
    /// <summary>F_SEAL_SEAL | F_SEAL_SHRINK | F_SEAL_GROW | F_SEAL_WRITE: no descriptor can change the bytes.</summary>
    internal const int ImmutableSeals = 0x1 | 0x2 | 0x4 | 0x8;
    private const uint CloseOnExec = 0x1, AllowSealing = 0x2, ExecutableMemfd = 0x10; // MFD_CLOEXEC, MFD_ALLOW_SEALING, MFD_EXEC
    private const int AddSeals = 1033, GetSeals = 1034; // F_ADD_SEALS, F_GET_SEALS
    private const int InvalidArgument = 22; // EINVAL: MFD_EXEC predates kernel support.

    private SealedExecutable(SafeFileHandle handle, long bytes, string sha256)
    {
        Handle = handle;
        Bytes = bytes;
        Sha256 = sha256;
    }

    internal SafeFileHandle Handle { get; }
    internal long Bytes { get; }
    internal string Sha256 { get; }
    internal int Descriptor => checked((int)Handle.DangerousGetHandle());

    /// <summary>Copies a regular, non-linked executable into a new sealed memfd and measures the sealed bytes.</summary>
    /// <exception cref="IOException">The source could not be read or the sealed image could not be created.</exception>
    /// <exception cref="InvalidDataException">The source is not a bounded regular file or its length changed.</exception>
    internal static SealedExecutable Seal(string path, string name, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Sealed executables require Linux memfd.");
        using SafeFileHandle source = NativeFileLease.OpenRegularRead(path);
        long length = RandomAccess.GetLength(source);
        if (length is < 1 or > MaximumBytes)
            throw new InvalidDataException("The executable exceeds the sealed image bound.");
        SafeFileHandle image = Create(name);
        try
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(64 << 10);
            try
            {
                for (long offset = 0; offset < length;)
                {
                    ct.ThrowIfCancellationRequested();
                    int read = RandomAccess.Read(source, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - offset)), offset);
                    if (read <= 0)
                        throw new InvalidDataException("The executable changed length while it was sealed.");
                    RandomAccess.Write(image, buffer.AsSpan(0, read), offset);
                    offset += read;
                }
                if (RandomAccess.GetLength(source) != length || RandomAccess.GetLength(image) != length)
                    throw new InvalidDataException("The executable changed length while it was sealed.");
                int descriptor = checked((int)image.DangerousGetHandle());
                if (NativeFileLease.Fcntl(descriptor, AddSeals, ImmutableSeals) != 0)
                    throw new IOException("The executable image could not be sealed.", new Win32Exception(Marshal.GetLastPInvokeError()));
                if (NativeFileLease.Fcntl(descriptor, GetSeals, 0) != ImmutableSeals)
                    throw new IOException("The executable image did not retain its seals.");
                // Measure what will execute: the sealed memfd, not the source path.
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                for (long offset = 0; offset < length;)
                {
                    ct.ThrowIfCancellationRequested();
                    int read = RandomAccess.Read(image, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - offset)), offset);
                    if (read <= 0)
                        throw new IOException("The sealed executable image could not be measured.");
                    hash.AppendData(buffer, 0, read);
                    offset += read;
                }
                return new(image, length, Convert.ToHexStringLower(hash.GetHashAndReset()));
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        }
        catch { image.Dispose(); throw; }
    }

    public void Dispose() => Handle.Dispose();

    private static SafeFileHandle Create(string name)
    {
        int descriptor = MemfdCreate(name, CloseOnExec | AllowSealing | ExecutableMemfd);
        if (descriptor < 0 && Marshal.GetLastPInvokeError() == InvalidArgument)
            descriptor = MemfdCreate(name, CloseOnExec | AllowSealing); // Before Linux 6.3, every memfd is executable.
        if (descriptor < 0)
            throw new IOException("A sealed executable image could not be created.", new Win32Exception(Marshal.GetLastPInvokeError()));
        return new SafeFileHandle(descriptor, ownsHandle: true);
    }

    [DllImport("libc", EntryPoint = "memfd_create", SetLastError = true)]
    private static extern int MemfdCreate([MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint flags);
}
