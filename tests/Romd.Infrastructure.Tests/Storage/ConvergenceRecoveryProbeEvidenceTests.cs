using System.ComponentModel;
using System.Text.Json;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Storage;
using Romd.Media.Processes;
using Romd.Media.Probe;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class ConvergenceRecoveryProbeEvidenceTests
{
    [Fact]
    public void FileIdentity_LiveOriginalLease_ObservesInodeWithoutChangingCustody()
    {
        if (OperatingSystem.IsWindows())
            return;
        string root = Directory.CreateTempSubdirectory("romd-probe-held-lease-").FullName;
        try
        {
            string path = Path.Combine(root, "slot-0.lease");
            using var owner = NativeFileLease.TryAcquire(path).ShouldNotBeNull();
            owner.Bind("0123456789abcdef0123456789abcdef");
            var original = owner.Identity;

            ConvergenceRecoveryChecks.FileIdentity(path).ShouldBe(original);

            owner.Identity.ShouldBe(original);
            new FileInfo(path).Length.ShouldBe(32);
            using var denied = NativeFileLease.TryAcquire(path);
            denied.ShouldBeNull("observation must leave the owner's exclusive lock held");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FileIdentity_MissingLease_RejectsWithoutCreatingPath()
    {
        if (OperatingSystem.IsWindows())
            return;
        string root = Directory.CreateTempSubdirectory("romd-probe-absent-lease-").FullName;
        try
        {
            string path = Path.Combine(root, "slot-0.lease");

            Should.Throw<Exception>(() => ConvergenceRecoveryChecks.FileIdentity(path));

            File.Exists(path).ShouldBeFalse();
            Directory.EnumerateFileSystemEntries(root).ShouldBeEmpty();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FileIdentity_SymbolicLink_RejectsWithoutFollowingOrReplacingLease()
    {
        if (OperatingSystem.IsWindows())
            return;
        string root = Directory.CreateTempSubdirectory("romd-probe-link-lease-").FullName;
        try
        {
            string path = Path.Combine(root, "slot-0.lease");
            string target = Path.Combine(root, "original.lease");
            File.WriteAllText(target, "retained");
            File.CreateSymbolicLink(path, target);

            var error = Should.Throw<Win32Exception>(() => ConvergenceRecoveryChecks.FileIdentity(path));

            error.NativeErrorCode.ShouldBe(OperatingSystem.IsMacOS() ? 62 : 40, "native open rejects the final symlink with ELOOP");

            new FileInfo(path).LinkTarget.ShouldBe(target);
            File.ReadAllText(target).ShouldBe("retained");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(MediaReconciliationOutcomeKind.Failed, "Rom.Media.Malformed")]
    [InlineData(MediaReconciliationOutcomeKind.Superseded, "kind-ineligible")]
    public void CaptureJobState_TerminalFailureWithoutErrors_PreservesFailureCode(
        MediaReconciliationOutcomeKind outcome, string failureCode)
    {
        var now = DateTimeOffset.UtcNow;
        var job = MediaReconciliationJob.Create(Guid.NewGuid(), new MediaReconciliationRequest.SubjectWork(new Romd.Domain.Source.Rom.MediaReconciliationSubject.LogicalRom(1)), "disc.iso", null, null, now);
        job.ApplyOutcome(outcome, failureCode, now);
        job.Errors.ShouldBeEmpty();

        using var report = JsonDocument.Parse(JsonSerializer.Serialize(ConvergenceRecoveryChecks.CaptureJobState(job)));

        report.RootElement.TryGetProperty("OutcomeCode", out var code).ShouldBeTrue();
        code.GetString().ShouldBe(failureCode);
        report.RootElement.GetProperty("Errors").GetArrayLength().ShouldBe(0);
        report.RootElement.GetProperty("Id").GetGuid().ShouldBe(job.Id);
    }
}
