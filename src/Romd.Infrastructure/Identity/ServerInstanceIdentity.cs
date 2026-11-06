using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Romd.Application.Common.Configuration;

namespace Romd.Infrastructure.Identity;

public sealed class ServerInstanceIdentity(IRomdOptions options) : IServerInstanceIdentity
{
    public const string RelativePath = "identity/server-instance-id";
    private const string TempFilePrefix = "server-instance-id.";
    private const string TempFileSuffix = ".tmp";

    private readonly Lock _lock = new();
    private Guid? _instanceId;

    public Guid InstanceId
    {
        get
        {
            lock (_lock)
            {
                return _instanceId ?? throw new InvalidOperationException(
                    "The ROMD server instance identity has not completed startup initialization.");
            }
        }
    }

    public static string ResolvePath(string dataDirectory) =>
        Path.Combine(dataDirectory, "identity", "server-instance-id");

    internal void Initialize()
    {
        lock (_lock)
        {
            _instanceId ??= LoadOrCreate(options.DataDirectory);
        }
    }

    private static Guid LoadOrCreate(string dataDirectory)
    {
        string identityPath = ResolvePath(dataDirectory);
        if (File.Exists(identityPath))
        {
            return ReadExistingAndReapOrphans(identityPath);
        }

        string identityDirectory = Path.GetDirectoryName(identityPath)!;
        Directory.CreateDirectory(identityDirectory);

        string canonicalId = Guid.NewGuid().ToString("D");
        string tempPath = Path.Combine(
            identityDirectory,
            $"{TempFilePrefix}{Guid.NewGuid():N}{TempFileSuffix}");

        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(canonicalId);
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            TryPublishWithoutClobber(tempPath, identityPath);

            return ReadExistingAndReapOrphans(identityPath);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A non-authoritative public temporary id may be cleaned up later. Never replace
                // or weaken validation of the authoritative identity because cleanup failed.
            }
        }
    }

    private static Guid ReadExistingAndReapOrphans(string identityPath)
    {
        // Validate the authority before deleting anything. A missing or corrupt authority must
        // never be disguised by temporary-file cleanup.
        Guid instanceId = ReadExisting(identityPath);
        ReapRecognizableOrphans(Path.GetDirectoryName(identityPath)!);
        return instanceId;
    }

    private static void ReapRecognizableOrphans(string identityDirectory)
    {
        foreach (string candidatePath in Directory.EnumerateFiles(
                     identityDirectory,
                     $"{TempFilePrefix}*{TempFileSuffix}",
                     SearchOption.TopDirectoryOnly))
        {
            string candidateName = Path.GetFileName(candidatePath);
            int tokenLength = candidateName.Length - TempFilePrefix.Length - TempFileSuffix.Length;
            if (tokenLength != 32)
            {
                continue;
            }

            string token = candidateName.Substring(TempFilePrefix.Length, tokenLength);
            if (!Guid.TryParseExact(token, "N", out Guid tempId) ||
                !string.Equals(tempId.ToString("N"), token, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                File.Delete(candidatePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A concurrent writer may still own this file, especially on Windows. Its owner
                // retries in its finally block; otherwise a later successful startup retries here.
            }
        }
    }

    private static void TryPublishWithoutClobber(string tempPath, string identityPath)
    {
        bool published = OperatingSystem.IsWindows()
            ? CreateHardLink(identityPath, tempPath, IntPtr.Zero)
            : Link(tempPath, identityPath) == 0;
        if (published)
        {
            return;
        }

        int errorCode = Marshal.GetLastWin32Error();
        if (File.Exists(identityPath))
        {
            // Another host atomically linked its complete same-volume temporary file first.
            return;
        }

        throw new IOException(
            $"Failed to atomically publish ROMD server instance identity at '{identityPath}'.",
            new Win32Exception(errorCode));
    }

    private static Guid ReadExisting(string identityPath)
    {
        byte[] bytes = File.ReadAllBytes(identityPath);
        string content = Encoding.ASCII.GetString(bytes);
        if (bytes.Length != 36 ||
            !Guid.TryParseExact(content, "D", out Guid instanceId) ||
            !string.Equals(instanceId.ToString("D"), content, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"ROMD server instance identity at '{identityPath}' is not a canonical lowercase UUID-D.");
        }

        return instanceId;
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string existingPath, string newPath);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string newFileName,
        string existingFileName,
        IntPtr securityAttributes);
}
