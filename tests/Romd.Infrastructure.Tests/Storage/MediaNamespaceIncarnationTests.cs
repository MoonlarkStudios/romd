using System.Reflection;
using Romd.Infrastructure.Storage;
using Romd.Media.Processes;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>Exercises recovery routing without granting a synthetic Linux lease on another platform.
/// Positive original-OFD extinction recovery is qualified separately in the Linux production image.</summary>
public sealed class MediaNamespaceIncarnationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("romd-namespace-incarnation-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("reused-label")]
    [InlineData("same-init-birth")]
    [InlineData("missing-old-birth")]
    [InlineData("unparseable-old-birth")]
    [InlineData("missing-current-birth")]
    [InlineData("unparseable-current-birth")]
    [InlineData("current-not-init")]
    [InlineData("foreign-boot")]
    [InlineData("child-namespace-mismatch")]
    [InlineData("missing-child-identity")]
    [InlineData("distinct-label")]
    [InlineData("init-wrapped-namespace-change")]
    [InlineData("local-parent-predates-init")]
    [InlineData("local-child-predates-init")]
    [InlineData("local-parent-missing-birth")]
    [InlineData("local-child-missing-birth")]
    [InlineData("local-parent-equal-init")]
    [InlineData("local-child-equal-init")]
    [InlineData("local-init-unknown")]
    public async Task RecoverCoreAsync_ForeignOrAmbiguousPid1Record_DoesNotQueryLocalPidOrReleaseCustody(string mutation)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (record, here) = CreateCase(mutation);
        string path = Path.Combine(_root, "run-0.json");
        MediaRunRecord.Write(path, record);
        byte[] journal = File.ReadAllBytes(path);
        var queriedPids = new List<int>();
        var runtime = new MediaProcessingRuntime(_root, Limits(), pid =>
        {
            queriedPids.Add(pid);
            return null; // Unknown deliberately prevents any actual signal if the unsafe route is taken.
        }, () => here);
        // This is deliberately NOT a NativeFileLease: the test cannot prove Linux
        // OFD extinction. Recovery must deny it without querying an old numeric PID.
        using var owner = new NonNativeOwner();
        var method = typeof(MediaProcessingRuntime).GetMethod("RecoverCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            .ShouldNotBeNull();

        bool recovered = await ((Task<bool>)method.Invoke(runtime, [record, path, owner, deadline.Token])!);

        recovered.ShouldBeFalse();
        queriedPids.ShouldBeEmpty("foreign or ambiguous PID1 records must never query a numeric PID in the current namespace");
        File.ReadAllBytes(path).ShouldBe(journal, "uncertain custody must remain durable");
    }

    [Theory]
    [InlineData("reused-label")]
    [InlineData("same-init-birth")]
    [InlineData("missing-old-birth")]
    [InlineData("unparseable-old-birth")]
    [InlineData("missing-current-birth")]
    [InlineData("unparseable-current-birth")]
    [InlineData("current-not-init")]
    [InlineData("foreign-boot")]
    [InlineData("child-namespace-mismatch")]
    [InlineData("missing-child-identity")]
    [InlineData("distinct-label")]
    [InlineData("init-wrapped-namespace-change")]
    [InlineData("local-parent-predates-init")]
    [InlineData("local-child-predates-init")]
    [InlineData("local-parent-missing-birth")]
    [InlineData("local-child-missing-birth")]
    [InlineData("local-parent-equal-init")]
    [InlineData("local-child-equal-init")]
    [InlineData("local-init-unknown")]
    public async Task TryStopOrphanAsync_ForeignOrAmbiguousPid1Record_DoesNotQueryOrSignalOldNumericIdentity(string mutation)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (record, here) = CreateCase(mutation);
        string path = Path.Combine(_root, "run-0.json");
        MediaRunRecord.Write(path, record);
        byte[] journal = File.ReadAllBytes(path);
        var queriedPids = new List<int>();
        var runtime = new MediaProcessingRuntime(_root, Limits(), pid =>
        {
            queriedPids.Add(pid);
            return null;
        }, () => here);
        var method = typeof(MediaProcessingRuntime).GetMethod("TryStopOrphanAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            .ShouldNotBeNull();

        await ((Task)method.Invoke(runtime, [0, deadline.Token])!);

        queriedPids.ShouldBeEmpty("namespace labels can be recycled; old parent/child PIDs must not be observed or signalled locally");
        File.ReadAllBytes(path).ShouldBe(journal);
    }

    private static MediaProcessingOptions Limits() => new() { MaxConcurrent = 1, AdmissionTimeoutSeconds = 1 };

    [Theory]
    [InlineData("0", "1", "2", true)]
    [InlineData("100", "101", "102", true)]
    [InlineData("100", "99", "102", false)]
    [InlineData("100", "101", "99", false)]
    [InlineData("100", "100", "102", false)]
    [InlineData("100", "101", "100", false)]
    [InlineData("0", "0", "2", false)]
    [InlineData("0", "1", "0", false)]
    [InlineData("unknown", "101", "102", false)]
    [InlineData("100", "unknown", "102", false)]
    [InlineData("100", "101", "unknown", false)]
    [InlineData("100", "18446744073709551616", "102", false)]
    [InlineData("100", "101", "18446744073709551616", false)]
    [InlineData("18446744073709551616", "101", "102", false)]
    [InlineData("100", " 101", "102", false)]
    [InlineData("100", "101", "+102", false)]
    [InlineData("-1", "101", "102", false)]
    public void HasLocalLinuxChronology_StartTicks_RequiresStrictlyLaterRecognizedProcesses(
        string initBirth, string parentBirth, string childBirth, bool expected)
    {
        var (record, here) = CreateCase("local-valid");

        MediaProcessingRuntime.HasLocalLinuxChronology(record.Parent with { Birth = parentBirth },
            record.Identity! with { Birth = childBirth }, here, here with { Birth = initBirth }).ShouldBe(expected);
    }

    [Theory]
    [InlineData("recover", "valid", true)]
    [InlineData("orphan", "valid", true)]
    [InlineData("recover", "zero-baseline", true)]
    [InlineData("orphan", "zero-baseline", true)]
    [InlineData("recover", "missing", false)]
    [InlineData("orphan", "missing", false)]
    [InlineData("recover", "boot-mismatch", false)]
    [InlineData("orphan", "boot-mismatch", false)]
    [InlineData("recover", "namespace-mismatch", false)]
    [InlineData("orphan", "namespace-mismatch", false)]
    [InlineData("recover", "not-init", false)]
    [InlineData("orphan", "not-init", false)]
    [InlineData("recover", "platform-mismatch", false)]
    [InlineData("orphan", "platform-mismatch", false)]
    [InlineData("recover", "unknown-birth", false)]
    [InlineData("orphan", "unknown-birth", false)]
    public async Task LocalRecovery_NonInitObserver_UsesSeparateBoundInitReaderBeforeOldPidLookup(
        string route, string mutation, bool permitsLookup)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (record, init) = CreateCase("local-valid");
        var here = init with { ProcessId = 42, Birth = "24254502" };
        MediaProcessIdentity? readInit = mutation switch
        {
            "missing" => null,
            "zero-baseline" => init with { Birth = "0" },
            "boot-mismatch" => init with { Boot = "different-boot" },
            "namespace-mismatch" => init with { PidNamespace = "pid:[4026533324]" },
            "not-init" => init with { ProcessId = 2 },
            "platform-mismatch" => init with { Platform = "darwin" },
            "unknown-birth" => init with { Birth = "unknown" },
            _ => init
        };
        string path = Path.Combine(_root, "run-0.json");
        MediaRunRecord.Write(path, record);
        byte[] journal = File.ReadAllBytes(path);
        int initReads = 0;
        var queriedPids = new List<int>();
        var runtime = new MediaProcessingRuntime(_root, Limits(), pid =>
        {
            initReads.ShouldBe(1, "current namespace init must be read before any retained numeric PID");
            queriedPids.Add(pid);
            return null; // Unknown stops the existing recovery checks before any signal or release.
        }, () => here, () => { initReads++; return readInit; });

        if (route == "recover")
        {
            using var owner = new NonNativeOwner();
            var method = typeof(MediaProcessingRuntime).GetMethod("RecoverCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic).ShouldNotBeNull();
            (await ((Task<bool>)method.Invoke(runtime, [record, path, owner, deadline.Token])!)).ShouldBeFalse();
        }
        else
        {
            var method = typeof(MediaProcessingRuntime).GetMethod("TryStopOrphanAsync", BindingFlags.Instance | BindingFlags.NonPublic).ShouldNotBeNull();
            await ((Task)method.Invoke(runtime, [0, deadline.Token])!);
        }

        initReads.ShouldBe(1);
        queriedPids.ShouldBe(permitsLookup ? new[] { route == "recover" ? record.Identity!.ProcessId : record.Parent.ProcessId } : []);
        File.ReadAllBytes(path).ShouldBe(journal);
    }

    private static (MediaRunRecord Record, MediaProcessIdentity Here) CreateCase(string mutation)
    {
        var parent = new MediaProcessIdentity(1, 1, "linux", "kernel-boot", "24251260", "pid:[4026533323]");
        var child = new MediaProcessIdentity(23, 23, "linux", parent.Boot, "24251300", parent.PidNamespace);
        var here = new MediaProcessIdentity(1, 1, "linux", parent.Boot, "24254496", parent.PidNamespace);
        var record = new MediaRunRecord(MediaRunRecord.CurrentVersion, Guid.NewGuid().ToString("N"), 0, 1,
            Guid.NewGuid(), "encode", "permitted", parent, "worker-build", null, child,
            new MediaAdmissionIdentity("linux", 31, 41), []);
        if (mutation == "same-init-birth")
            here = here with { Birth = parent.Birth };
        if (mutation == "missing-old-birth")
            record = record with { Parent = parent with { Birth = string.Empty } };
        if (mutation == "unparseable-old-birth")
            record = record with { Parent = parent with { Birth = "old-init" } };
        if (mutation == "missing-current-birth")
            here = here with { Birth = string.Empty };
        if (mutation == "unparseable-current-birth")
            here = here with { Birth = "new-init" };
        if (mutation == "current-not-init")
            here = here with { ProcessId = 13 };
        if (mutation == "foreign-boot")
            here = here with { Boot = "other-kernel-boot" };
        if (mutation == "child-namespace-mismatch")
            record = record with { Identity = child with { PidNamespace = "pid:[4026533325]" } };
        if (mutation == "missing-child-identity")
            record = record with { Identity = null };
        if (mutation == "distinct-label")
            here = here with { PidNamespace = "pid:[4026533324]" };
        // Earlier/custom `init: true` deployments: neither the recorded nor the
        // recreated worker is PID 1. These rows assert only that recovery queries no local PID and keeps the journal
        // durable for that shape; with a non-native owner they return before the PID 1 rule. The discriminating
        // extinction row is ForeignMediaCustodyTests' init-wrapped-namespace-change.
        if (mutation == "init-wrapped-namespace-change")
        {
            record = record with { Parent = parent with { ProcessId = 7, GroupId = 7 } };
            here = here with { ProcessId = 7, GroupId = 7, PidNamespace = "pid:[4026533324]" };
        }
        if (mutation.StartsWith("local-", StringComparison.Ordinal))
        {
            record = record with
            {
                Parent = parent with { ProcessId = 13, Birth = "24254500" },
                Identity = child with { Birth = "24254501" }
            };
            if (mutation == "local-parent-predates-init")
                record = record with { Parent = record.Parent with { Birth = "24251260" } };
            if (mutation == "local-child-predates-init")
                record = record with { Identity = record.Identity! with { Birth = "24251300" } };
            if (mutation == "local-parent-missing-birth")
                record = record with { Parent = record.Parent with { Birth = string.Empty } };
            if (mutation == "local-child-missing-birth")
                record = record with { Identity = record.Identity! with { Birth = string.Empty } };
            if (mutation == "local-parent-equal-init")
                record = record with { Parent = record.Parent with { Birth = here.Birth } };
            if (mutation == "local-child-equal-init")
                record = record with { Identity = record.Identity! with { Birth = here.Birth } };
            if (mutation == "local-init-unknown")
                here = here with { Birth = "unknown-init" };
        }
        return (record, here);
    }

    private sealed class NonNativeOwner : IDisposable
    {
        public void Dispose() { }
    }
}
