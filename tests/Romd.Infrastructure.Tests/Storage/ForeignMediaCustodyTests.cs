using Romd.Infrastructure.Storage;
using Romd.Media.Processes;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>Contract decisions; actual Linux namespace recreation is qualified by the production probe.</summary>
public sealed class ForeignMediaCustodyTests
{
    [Theory]
    [InlineData("valid", true)]
    [InlineData("reused-namespace-label", true)]
    [InlineData("distinct-label-current-not-init", true)]
    [InlineData("same-init-birth", false)]
    [InlineData("missing-old-init-birth", false)]
    [InlineData("unparseable-old-init-birth", false)]
    [InlineData("missing-current-init-birth", false)]
    [InlineData("unparseable-current-init-birth", false)]
    [InlineData("zero-current-init-birth", false)]
    [InlineData("overflow-current-init-birth", false)]
    [InlineData("whitespace-current-init-birth", false)]
    [InlineData("current-not-init", false)]
    [InlineData("parent-not-init", false)]
    [InlineData("init-wrapped-namespace-change", false)]
    [InlineData("child-other-namespace", false)]
    [InlineData("foreign-boot", false)]
    [InlineData("parent-child-boot-mismatch", false)]
    [InlineData("replaced-inode", false)]
    [InlineData("legacy-version", false)]
    [InlineData("unknown-namespace", false)]
    [InlineData("unknown-admission", false)]
    public void ForeignNamespaceExtinct_RequiresApprovedInitIncarnationAndExactOriginalAdmissionFacts(string mutation, bool expected)
    {
        var admission = new MediaAdmissionIdentity("linux", 31, 41);
        var parent = new MediaProcessIdentity(1, 1, "linux", "kernel-boot", "24251260", "pid:[4026533323]");
        var child = new MediaProcessIdentity(23, 23, "linux", "kernel-boot", "24251300", parent.PidNamespace);
        var here = new MediaProcessIdentity(1, 1, "linux", "kernel-boot", "24254496", "pid:[4026533324]");
        var record = new MediaRunRecord(MediaRunRecord.CurrentVersion, Guid.NewGuid().ToString("N"), 0, 1,
            Guid.NewGuid(), "encode", "permitted", parent, "worker-build", null, child, admission, []);
        if (mutation == "reused-namespace-label")
            here = here with { PidNamespace = parent.PidNamespace };
        if (mutation is "same-init-birth" or "missing-old-init-birth" or "unparseable-old-init-birth" or
            "missing-current-init-birth" or "unparseable-current-init-birth" or "zero-current-init-birth" or
            "overflow-current-init-birth" or "whitespace-current-init-birth")
        {
            here = here with { PidNamespace = parent.PidNamespace };
            if (mutation == "same-init-birth")
                here = here with { Birth = parent.Birth };
            if (mutation == "missing-old-init-birth")
                record = record with { Parent = parent with { Birth = string.Empty } };
            if (mutation == "unparseable-old-init-birth")
                record = record with { Parent = parent with { Birth = "old-init" } };
            if (mutation == "missing-current-init-birth")
                here = here with { Birth = string.Empty };
            if (mutation == "unparseable-current-init-birth")
                here = here with { Birth = "new-init" };
            if (mutation == "zero-current-init-birth")
                here = here with { Birth = "0" };
            if (mutation == "overflow-current-init-birth")
                here = here with { Birth = "18446744073709551616" };
            if (mutation == "whitespace-current-init-birth")
                here = here with { Birth = " 24254496" };
        }
        if (mutation == "current-not-init")
            here = here with { ProcessId = 13, PidNamespace = parent.PidNamespace };
        if (mutation == "distinct-label-current-not-init")
            here = here with { ProcessId = 13 };
        if (mutation == "parent-not-init")
            record = record with { Parent = parent with { ProcessId = 13 } };
        // Earlier/custom `init: true` deployments: docker-init is PID 1, so
        // neither the old nor the recreated worker is. This exercises the same PID 1 parent branch as parent-not-init,
        // with the real topology's non-init current process: custody stays pinned after a container recreation.
        if (mutation == "init-wrapped-namespace-change")
        {
            record = record with { Parent = parent with { ProcessId = 7, GroupId = 7 } };
            here = here with { ProcessId = 7, GroupId = 7 };
        }
        if (mutation == "child-other-namespace")
            child = child with { PidNamespace = "pid:[4026533325]" };
        if (mutation == "foreign-boot")
            here = here with { Boot = "other-kernel-boot" };
        if (mutation == "parent-child-boot-mismatch")
            child = child with { Boot = "other-kernel-boot" };
        if (mutation == "replaced-inode")
            admission = admission with { Inode = 42 };
        if (mutation == "legacy-version")
            record = record with { Version = "romd-media-run/v1" };
        if (mutation == "unknown-namespace")
            child = child with { PidNamespace = string.Empty };
        if (mutation == "unknown-admission")
            record = record with { Admission = null };

        MediaProcessingRuntime.ForeignNamespaceExtinct(record, child, here, admission).ShouldBe(expected);
    }
}
