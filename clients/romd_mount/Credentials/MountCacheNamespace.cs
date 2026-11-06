using System.Security.Cryptography;
using System.Text;

namespace Romd.Mount.Credentials;

// Caller holds the global credential lease and has rejected mount reservations.
// Shared with every metadata root because they all read the same Keychain item.
internal static class MountCacheNamespace
{
    internal static string DirectoryPath => Path.Combine(MountProfileLease.CredentialLockDirectory, "cache-namespaces");

    internal static string GetOrCreateKey(string directory, string credentialKey)
    {
        string path = RecordPath(directory, credentialKey);
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("Cache namespace must be an ordinary file.");
        }
        catch (FileNotFoundException) { return Rotate(directory, credentialKey); }
        using var stream = File.OpenRead(path);
        Span<byte> bytes = stackalloc byte[65];
        int count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (count != 64 || bytes[..count].ContainsAnyExcept("0123456789abcdef"u8))
            throw new IOException("Invalid cache namespace.");
        return Key(credentialKey, Encoding.ASCII.GetString(bytes[..count]));
    }

    internal static string Rotate(string directory, string credentialKey)
    {
        string path = RecordPath(directory, credentialKey);
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("Cache namespace is a symbolic link.");
        string generation = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(Encoding.ASCII.GetBytes(generation));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return Key(credentialKey, generation);
    }

    private static string RecordPath(string directory, string key)
    {
        if (key.Length != 64 || key.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Invalid credential key.");
        PrivateStateDirectory.Ensure(directory);
        return Path.Combine(directory, key + ".txt");
    }

    private static string Key(string credentialKey, string generation) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(credentialKey + generation)));
}
