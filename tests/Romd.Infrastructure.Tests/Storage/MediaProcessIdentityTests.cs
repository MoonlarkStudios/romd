using System.Globalization;
using Romd.Infrastructure.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class MediaProcessIdentityTests
{
    [Theory]
    [InlineData("Name:\tworker\nNSpid:\t42\n", 42, true)]
    [InlineData("Name:\tworker\nNSpid:\t7\t42\n", 42, false)]
    [InlineData("Name:\tworker\nNSpid:\t41\n", 42, false)]
    [InlineData("Name:\tworker\nPid:\t42\n", 42, false)]
    [InlineData("Name:\tworker\nNSpid:\t\n", 42, false)]
    [InlineData("Name:\tworker\nNSpid:\t+42\n", 42, false)]
    [InlineData("NSpid:\t42\nNSpid:\t42\n", 42, false)]
    public void IsSolePidNamespaceMember_StatusShapes_AcceptsOnlyOneMatchingEntry(string status, int pid, bool expected) =>
        MediaProcessIdentity.IsSolePidNamespaceMember(status, pid).ShouldBe(expected);

    [Fact]
    public void ReadNamespaceInit_Linux_BindsPidOneToThisProcessNamespace()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var here = MediaProcessIdentity.Read(Environment.ProcessId).ShouldNotBeNull();

        var init = MediaProcessIdentity.ReadNamespaceInit()
            .ShouldNotBeNull("this namespace's init is identified even when its namespace link is unreadable");

        init.ProcessId.ShouldBe(1);
        init.Platform.ShouldBe("linux");
        init.Boot.ShouldBe(here.Boot);
        init.PidNamespace.ShouldBe(here.PidNamespace);
        string stat = File.ReadAllText("/proc/1/stat");
        init.Birth.ShouldBe(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[19], "the init's own start ticks, not this process's");
        ulong.Parse(init.Birth, CultureInfo.InvariantCulture).ShouldBeLessThanOrEqualTo(ulong.Parse(here.Birth, CultureInfo.InvariantCulture));
    }
}
