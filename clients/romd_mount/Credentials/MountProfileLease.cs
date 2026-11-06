namespace Romd.Mount.Credentials;

/// <summary>
/// Exclusive process ownership for a credential scope. Hold for the entire broker/mount
/// lifetime. Lock files persist after release; deleting them could split competing owners.
/// </summary>
public sealed class MountProfileLease : IDisposable
{
    private readonly FileStream _file;
    private MountProfileLease(FileStream file) => _file = file;

    public static string CredentialLockDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ROMD", "Mount", "credential-locks");

    // Keychain scope is independent of metadata-root overrides, so ownership must be too.
    public static MountProfileLease AcquireCredential(MountCredentialScope scope) => Acquire(CredentialLockDirectory, scope.Key);

    public static MountProfileLease Acquire(string directory, string credentialKey)
    {
        if (credentialKey.Length != 64 || credentialKey.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Invalid credential key.", nameof(credentialKey));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (new DirectoryInfo(directory).LinkTarget is not null)
            throw new IOException("The profile lock directory must not be a symbolic link.");
        if (!OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(directory) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new IOException("The profile lock directory must be private to its owner.");

        string path = Path.Combine(directory, credentialKey + ".lock");
        if (new FileInfo(path).LinkTarget is not null)
            throw new IOException("The profile lock file must not be a symbolic link.");
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new MountProfileLease(new FileStream(path, options));
    }

    public void Dispose() => _file.Dispose();
}
