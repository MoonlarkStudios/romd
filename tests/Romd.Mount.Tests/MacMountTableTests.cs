using Romd.Mount.Mounting;
using Shouldly;
using Xunit;

namespace Romd.Mount.Tests;

public sealed class MacMountTableTests
{
    [MacStateAclTests.MacAclFact]
    public void KernelSnapshot_ReportsStableRootAndBootWithoutTouchingMissingPaths()
    {
        MacMountTable.BootSession().ShouldNotBe(Guid.Empty);
        MacMountTable.BootSession().ShouldBe(MacMountTable.BootSession());
        var root = MacMountTable.Find("/");
        root.ShouldNotBeNull();
        root.ShouldBe(MacMountTable.Find("/"));
        root.FileSystem.ShouldNotBeNullOrWhiteSpace();
        root.Source.ShouldNotBeNullOrWhiteSpace();
        MacMountTable.Find("/romd-absent-" + Guid.NewGuid().ToString("N")).ShouldBeNull();
    }
}
