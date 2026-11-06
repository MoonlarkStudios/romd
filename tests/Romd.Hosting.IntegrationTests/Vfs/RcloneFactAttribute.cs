using Xunit;

namespace Romd.Hosting.IntegrationTests.Vfs;

/// <summary>External-client evidence is explicit; absent tooling must be reported as skipped.</summary>
public sealed class RcloneFactAttribute : FactAttribute
{
    public RcloneFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("ROMD_VFS_RCLONE")))
            Skip = "Set ROMD_VFS_RCLONE to a verified rclone executable to run the real-client probe.";
    }
}

public sealed class RcloneMountFactAttribute : FactAttribute
{
    public RcloneMountFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("ROMD_VFS_MOUNT") != "1" ||
            !File.Exists(Environment.GetEnvironmentVariable("ROMD_VFS_RCLONE")))
            Skip = "macOS mount probe requires ROMD_VFS_MOUNT=1 and ROMD_VFS_RCLONE; it mounts and unmounts an isolated read-only test directory.";
    }
}

public sealed class RcloneTokenCommandFactAttribute : FactAttribute
{
    public RcloneTokenCommandFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("ROMD_VFS_RCLONE")) ||
            !File.Exists(Environment.GetEnvironmentVariable("ROMD_VFS_PYTHON") ?? "/usr/bin/python3"))
            Skip = "Token-command probe requires ROMD_VFS_RCLONE and Python (ROMD_VFS_PYTHON or /usr/bin/python3).";
    }
}

public sealed class RcloneKeychainFactAttribute : FactAttribute
{
    public RcloneKeychainFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("ROMD_MOUNT_KEYCHAIN_TEST") != "1" ||
            !File.Exists(Environment.GetEnvironmentVariable("ROMD_VFS_RCLONE")) ||
            !File.Exists(Environment.GetEnvironmentVariable("ROMD_VFS_PYTHON") ?? "/usr/bin/python3"))
            Skip = "Native credential probe requires macOS, ROMD_MOUNT_KEYCHAIN_TEST=1, ROMD_VFS_RCLONE and Python.";
    }
}

public sealed class RegisteredFskitFactAttribute : FactAttribute
{
    public RegisteredFskitFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("ROMD_VFS_REGISTERED_FSKIT") != "1" ||
            !File.Exists(Environment.GetEnvironmentVariable("ROMD_VFS_RCLONE")))
            Skip = "Requires macOS with enabled macFUSE FSKit, ROMD_VFS_REGISTERED_FSKIT=1 and ROMD_VFS_RCLONE; mounts an isolated private home directory.";
    }
}
