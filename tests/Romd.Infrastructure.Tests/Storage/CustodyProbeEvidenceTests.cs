using System.ComponentModel;
using System.Text.Json;
using System.Security.Cryptography;
using Romd.Infrastructure.Storage;
using Romd.Media.Probe;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class CustodyProbeEvidenceTests
{
    [Fact]
    public void Witness_LiveExclusiveOutput_ObservesBoundedPrefixWithoutChangingWriterOrLock()
    {
        if (OperatingSystem.IsWindows())
            return;
        string root = Directory.CreateTempSubdirectory("romd-custody-live-output-").FullName;
        try
        {
            string path = Path.Combine(root, "slot-1.data");
            byte[] bytes = Enumerable.Range(0, 131072).Select(value => (byte)value).ToArray();
            using var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            writer.Write(bytes);
            writer.Flush(flushToDisk: true);
            long position = writer.Position;

            var witness = CustodyChecks.Witness(path);

            witness.Exists.ShouldBeTrue();
            witness.Bytes.ShouldBe(bytes.Length);
            witness.FileIdentity.ShouldBe(ConvergenceRecoveryChecks.FileIdentity(path));
            witness.PrefixSha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(0, 65536))));
            writer.Position.ShouldBe(position);
            Should.Throw<IOException>(() => { using var contender = File.OpenRead(path); });
            writer.WriteByte(17);
            writer.Flush(flushToDisk: true);
            CustodyChecks.Witness(path).Bytes.ShouldBe(bytes.Length + 1);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Witness_MissingOutput_ReportsAbsentWithoutCreatingFile()
    {
        string root = Directory.CreateTempSubdirectory("romd-custody-absent-output-").FullName;
        try
        {
            string path = Path.Combine(root, "slot-1.data");

            var witness = CustodyChecks.Witness(path);

            witness.ShouldBe(new(path, false, 0, null, null));
            Directory.EnumerateFileSystemEntries(root).ShouldBeEmpty();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Witness_SymbolicLink_RejectsWithoutFollowingOrReplacingOutput()
    {
        if (OperatingSystem.IsWindows())
            return;
        string root = Directory.CreateTempSubdirectory("romd-custody-linked-output-").FullName;
        try
        {
            string path = Path.Combine(root, "slot-1.data");
            string target = Path.Combine(root, "original.data");
            File.WriteAllText(target, "retained");
            File.CreateSymbolicLink(path, target);

            var error = Should.Throw<Win32Exception>(() => CustodyChecks.Witness(path));

            error.NativeErrorCode.ShouldBe(OperatingSystem.IsMacOS() ? 62 : 40, "native open rejects the final symlink with ELOOP");

            new FileInfo(path).LinkTarget.ShouldBe(target);
            File.ReadAllText(target).ShouldBe("retained");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(MediaWorkerFailure.IoFailure)]
    [InlineData(MediaWorkerFailure.Protocol)]
    public void CaptureFailure_EncodingRejected_PreservesUnderlyingResultAndCheckpointMessage(MediaWorkerFailure code)
    {
        var encoding = new MediaWorkerResult(null, code, new(31, 137, 0, 0, MediaWorkerDiagnosticKind.None), true);
        var error = new IOException("checkpoint witness could not be opened");

        using var report = JsonDocument.Parse(JsonSerializer.Serialize(CustodyChecks.CaptureFailure(error, null, encoding)));

        report.RootElement.TryGetProperty("FailureMessage", out var message).ShouldBeTrue();
        message.GetString().ShouldBe(error.Message);
        report.RootElement.GetProperty("Encoding").GetProperty("Failure").GetInt32().ShouldBe((int)code);
        report.RootElement.GetProperty("Encoding").GetProperty("ProcessTreeStopped").GetBoolean().ShouldBeTrue();
        report.RootElement.GetProperty("Inspection").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void CaptureFailure_LongCheckpointMessage_RemainsBounded()
    {
        using var report = JsonDocument.Parse(JsonSerializer.Serialize(
            CustodyChecks.CaptureFailure(new IOException(new string('x', 16384)), null, null)));

        report.RootElement.TryGetProperty("FailureMessage", out var message).ShouldBeTrue();
        message.GetString()!.Length.ShouldBe(4096);
    }
}
