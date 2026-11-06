using Shouldly;
using Xunit;

namespace Romd.Mount.Tests;

public sealed class PrivateStateDirectoryTests
{
    [Fact]
    public void Ensure_CreatesPrivateDirectoryAndReusesIt()
    {
        if (OperatingSystem.IsWindows())
            return;
        string root = Path.Combine(Path.GetTempPath(), "romd-private-" + Guid.NewGuid().ToString("N"));
        try
        {
            PrivateStateDirectory.Ensure(root).ShouldBe(root);
            File.GetUnixFileMode(root).ShouldBe(PrivateStateDirectory.OwnerOnly);
            File.WriteAllText(Path.Combine(root, "retained"), "existing cached content");
            PrivateStateDirectory.Ensure(root).ShouldBe(root);
            File.ReadAllText(Path.Combine(root, "retained")).ShouldBe("existing cached content");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Ensure_RejectsExistingPublicDirectoryWithoutChangingPermissionsOrContent()
    {
        if (OperatingSystem.IsWindows())
            return;
        string root = Path.Combine(Path.GetTempPath(), "romd-public-" + Guid.NewGuid().ToString("N"));
        var mode = PrivateStateDirectory.OwnerOnly | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        try
        {
            Directory.CreateDirectory(root);
            File.SetUnixFileMode(root, mode);
            File.WriteAllText(Path.Combine(root, "retained"), "keep");
            Should.Throw<IOException>(() => PrivateStateDirectory.Ensure(root));
            File.GetUnixFileMode(root).ShouldBe(mode);
            File.ReadAllText(Path.Combine(root, "retained")).ShouldBe("keep");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Ensure_RejectsLinksIncludingDanglingLinksWithoutCreatingTargets()
    {
        if (OperatingSystem.IsWindows())
            return;
        string root = Path.Combine(Path.GetTempPath(), "romd-linked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string target = Path.Combine(root, "target");
        string link = Path.Combine(root, "link");
        try
        {
            Directory.CreateSymbolicLink(link, target);
            Should.Throw<IOException>(() => PrivateStateDirectory.Ensure(link));
            Directory.Exists(target).ShouldBeFalse();
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "retained"), "keep");
            Should.Throw<IOException>(() => PrivateStateDirectory.Ensure(link));
            File.ReadAllText(Path.Combine(target, "retained")).ShouldBe("keep");
        }
        finally { Directory.Delete(link); Directory.Delete(root, true); }
    }
}
