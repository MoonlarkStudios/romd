using System.Diagnostics;
using Shouldly;
using Xunit;

namespace Romd.Mount.Tests;

public sealed class MacStateAclTests
{
    [MacAclFact]
    public async Task OwnerOnlyMode_WithPublicAcl_IsRejectedWithoutRemovingAcl()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        string root = Path.Combine(Path.GetTempPath(), "romd-acl-" + Guid.NewGuid().ToString("N"));
        try
        {
            PrivateStateDirectory.Ensure(root);
            var start = new ProcessStartInfo("/bin/chmod") { UseShellExecute = false, RedirectStandardError = true };
            foreach (string value in new[] { "+a", "everyone allow list,search,readattr", root })
                start.ArgumentList.Add(value);
            using var process = Process.Start(start)!;
            Task<string> error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            process.ExitCode.ShouldBe(0, await error);
            File.GetUnixFileMode(root).ShouldBe(PrivateStateDirectory.OwnerOnly);
            Should.Throw<IOException>(() => PrivateStateDirectory.Ensure(root)).Message.ShouldContain("extended ACL");
            // A second rejection proves validation did not silently strip the entry.
            Should.Throw<IOException>(() => PrivateStateDirectory.Ensure(root)).Message.ShouldContain("extended ACL");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root); }
    }

    public sealed class MacAclFactAttribute : FactAttribute
    {
        public MacAclFactAttribute()
        {
            if (!OperatingSystem.IsMacOS())
                Skip = "Requires native macOS extended ACLs.";
        }
    }
}
