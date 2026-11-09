using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Romd.Infrastructure.Storage;
using Romd.Media.Processes;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class PosixMediaToolTests(PosixMediaToolFixture fixture) : IClassFixture<PosixMediaToolFixture>
{
    [Fact]
    public async Task TryObserveOwned_LiveHeldChild_ReturnsExactPidAndCallbackFacts()
    {
        if (AssertUnsupportedPlatform())
            return;
        string root = Directory.CreateTempSubdirectory("romd-tool-observe-").FullName;
        PosixMediaChild? tool = null;
        try
        {
            using var owner = NativeFileLease.TryAcquire(Path.Combine(root, "processing.lease")).ShouldNotBeNull();
            owner.Bind(Guid.NewGuid().ToString("N"));
            tool = PosixMediaChild.StartTool(fixture.Start("hold"), owner.Descriptor, []);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await ReadReportAsync(tool, deadline.Token);
            int calls = 0;
            tool.TryObserveOwned(pid => { calls++; pid.ShouldBe(tool.Id); return "observed"; }, out string facts).ShouldBeTrue();
            calls.ShouldBe(1);
            facts.ShouldBe("observed");
            tool.HasExited.ShouldBeFalse();
        }
        finally { if (tool is not null) { await tool.TerminateAndWaitAsync(); tool.Dispose(); } Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task TryObserveOwned_JoinedChild_DoesNotInvokeCallbackForRetiredPid()
    {
        if (AssertUnsupportedPlatform())
            return;
        string root = Directory.CreateTempSubdirectory("romd-tool-retired-").FullName;
        PosixMediaChild? tool = null;
        try
        {
            using var owner = NativeFileLease.TryAcquire(Path.Combine(root, "processing.lease")).ShouldNotBeNull();
            owner.Bind(Guid.NewGuid().ToString("N"));
            tool = PosixMediaChild.StartTool(fixture.Start("report"), owner.Descriptor, []);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await ReadReportAsync(tool, deadline.Token);
            await tool.WaitAsync(deadline.Token);
            int calls = 0;
            tool.TryObserveOwned(pid => { calls++; return pid; }, out int facts).ShouldBeFalse();
            calls.ShouldBe(0);
            facts.ShouldBe(0);
            await AssertExitAsync(tool, 37, deadline.Token);
        }
        finally { if (tool is not null) { await tool.TerminateAndWaitAsync(); tool.Dispose(); } Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task TryObserveOwned_CallbackThrows_RetainsExactTerminationWaitAndLeaseRelease()
    {
        if (AssertUnsupportedPlatform())
            return;
        string root = Directory.CreateTempSubdirectory("romd-tool-observer-failure-").FullName;
        PosixMediaChild? tool = null;
        try
        {
            string path = Path.Combine(root, "processing.lease");
            using var owner = NativeFileLease.TryAcquire(path).ShouldNotBeNull();
            owner.Bind(Guid.NewGuid().ToString("N"));
            var identity = owner.Identity;
            tool = PosixMediaChild.StartTool(fixture.Start("hold"), owner.Descriptor, []);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await ReadReportAsync(tool, deadline.Token);
            var failure = new IOException("injected owned image read failure");
            Should.Throw<IOException>(() => tool.TryObserveOwned<int>(_ => throw failure, out _)).ShouldBeSameAs(failure);
            tool.HasExited.ShouldBeFalse();
            owner.Dispose();
            using (var denied = NativeFileLease.TryAcquire(path))
                denied.ShouldBeNull();
            await tool.TerminateAndWaitAsync(deadline.Token);
            tool.ExitCode.ShouldBe(137);
            MediaProcessIdentity.Observe(tool.Id).Presence.ShouldBe(MediaProcessPresence.Gone);
            using var acquired = NativeFileLease.TryAcquire(path).ShouldNotBeNull();
            acquired.Identity.ShouldBe(identity);
        }
        finally { if (tool is not null) { await tool.TerminateAndWaitAsync(); tool.Dispose(); } Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task StartTool_SharedDescription_ClosingParentKeepsAdmissionUntilToolJoins()
    {
        if (AssertUnsupportedPlatform())
            return;
        string root = Directory.CreateTempSubdirectory("romd-tool-ofd-").FullName;
        PosixMediaChild? tool = null;
        using var owner = NativeFileLease.TryAcquire(Path.Combine(root, "processing.lease")).ShouldNotBeNull();
        string nonce = Guid.NewGuid().ToString("N");
        owner.Bind(nonce);
        var identity = owner.Identity;
        try
        {
            int nonblocking = OperatingSystem.IsMacOS() ? 4 : 0x800;
            int originalFlags = NativeFileLease.Fcntl(owner.Descriptor, 3, 0);
            originalFlags.ShouldBeGreaterThanOrEqualTo(0);
            (originalFlags & nonblocking).ShouldBe(0);
            tool = PosixMediaChild.StartTool(fixture.Start("hold"), owner.Descriptor, []);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var report = await ReadReportAsync(tool, deadline.Token);
            report.Nonce.ShouldBe(nonce);
            report.LeaseFlags.ShouldBe(0);
            report.StatusFlags.ShouldBe(originalFlags | nonblocking);
            NativeFileLease.Fcntl(owner.Descriptor, 3, 0).ShouldBe(originalFlags | nonblocking,
                "the child's status change must affect the same open description, not a reopened pathname");
            owner.Dispose();
            using (var denied = NativeFileLease.TryAcquire(Path.Combine(root, "processing.lease")))
                denied.ShouldBeNull("the tool's inherited description must retain the original flock after parent close");
            await tool.Input.WriteAsync(new byte[] { 1 }, deadline.Token);
            tool.Input.Dispose();
            await tool.WaitAsync(deadline.Token);
            await AssertExitAsync(tool, 37, deadline.Token);
            using var independent = NativeFileLease.TryAcquire(Path.Combine(root, "processing.lease")).ShouldNotBeNull();
            independent.Identity.ShouldBe(identity);
        }
        finally { if (tool is not null) { await tool.TerminateAndWaitAsync(); tool.Dispose(); } Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task StartTool_OmitsReadyAndUnrelatedDescriptors_BeforeManagedStartup()
    {
        if (AssertUnsupportedPlatform())
            return;
        string root = Directory.CreateTempSubdirectory("romd-tool-fds-").FullName;
        PosixMediaChild? tool = null;
        int extra = -1;
        try
        {
            using var owner = NativeFileLease.TryAcquire(Path.Combine(root, "processing.lease")).ShouldNotBeNull();
            extra = NativeFileLease.Fcntl(owner.Descriptor, OperatingSystem.IsMacOS() ? 67 : 1030, 10);
            extra.ShouldBeGreaterThanOrEqualTo(10);
            NativeFileLease.Fcntl(extra, 2, 0).ShouldBe(0);
            var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            // Native shell checks run before CLR is allowed to reuse low descriptors.
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add($"if ( : >&4 ) 2>/dev/null; then exit 86; fi; if ( : >&{extra.ToString(CultureInfo.InvariantCulture)} ) 2>/dev/null; then exit 87; fi; printf descriptors-closed");
            start.Environment.Clear();
            tool = PosixMediaChild.StartTool(start, owner.Descriptor, []);
            tool.Input.Dispose();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var output = new StreamReader(tool.Output, leaveOpen: true);
            using var error = new StreamReader(tool.Error, leaveOpen: true);
            Task<string> stdout = output.ReadToEndAsync(deadline.Token), stderr = error.ReadToEndAsync(deadline.Token);
            await tool.WaitAsync(deadline.Token);
            tool.ExitCode.ShouldBe(0);
            (await stdout).ShouldBe("descriptors-closed");
            (await stderr).ShouldBeEmpty();
        }
        finally
        {
            if (tool is not null)
            { await tool.TerminateAndWaitAsync(); tool.Dispose(); }
            if (extra >= 0)
                NativeFileLease.Close(extra);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartTool_ExplicitArgumentsEnvironmentAndGroup_MatchActualPayload()
    {
        if (AssertUnsupportedPlatform())
            return;
        string root = Directory.CreateTempSubdirectory("romd-tool-argv-").FullName;
        PosixMediaChild? tool = null;
        try
        {
            using var owner = NativeFileLease.TryAcquire(Path.Combine(root, "processing.lease")).ShouldNotBeNull();
            owner.Bind(Guid.NewGuid().ToString("N"));
            string[] expected = ["argument with spaces", "$(not-a-command)", "", "--literal=value"];
            var parent = MediaProcessIdentity.Read(Environment.ProcessId).ShouldNotBeNull();
            tool = PosixMediaChild.StartTool(fixture.Start("report", expected), owner.Descriptor, []);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var report = await ReadReportAsync(tool, deadline.Token);
            report.Pid.ShouldBe(tool.Id);
            report.GroupId.ShouldBe(parent.GroupId);
            report.Arguments.ShouldBe(expected);
            report.Environment["ROMD_POSIX_TOOL_FIXTURE"].ShouldBe("explicit value");
            string[] environmentKeys = ["ROMD_POSIX_TOOL_FIXTURE"];
            if (OperatingSystem.IsMacOS())
            {
                report.Environment["TMPDIR"].ShouldBe(fixture.TemporaryDirectory);
                environmentKeys = [.. environmentKeys, "TMPDIR"];
            }
            report.Environment.Keys.Where(key => !(OperatingSystem.IsMacOS() && key == "__CF_USER_TEXT_ENCODING"))
                .Order(StringComparer.Ordinal).ShouldBe(environmentKeys);
            await tool.WaitAsync(deadline.Token);
            await AssertExitAsync(tool, 37, deadline.Token);
        }
        finally { if (tool is not null) { await tool.TerminateAndWaitAsync(); tool.Dispose(); } Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task WaitAsync_CancelledWaiter_DoesNotReapOrCancelOtherWaiters()
    {
        if (AssertUnsupportedPlatform())
            return;
        string root = Directory.CreateTempSubdirectory("romd-tool-wait-").FullName;
        PosixMediaChild? tool = null;
        try
        {
            using var owner = NativeFileLease.TryAcquire(Path.Combine(root, "processing.lease")).ShouldNotBeNull();
            owner.Bind(Guid.NewGuid().ToString("N"));
            tool = PosixMediaChild.StartTool(fixture.Start("hold"), owner.Descriptor, []);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await ReadReportAsync(tool, deadline.Token);
            using var cancelled = new CancellationTokenSource();
            Task interrupted = tool.WaitAsync(cancelled.Token);
            interrupted.IsCompleted.ShouldBeFalse();
            cancelled.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => interrupted);
            tool.HasExited.ShouldBeFalse();
            Task first = tool.WaitAsync(deadline.Token), second = tool.WaitAsync(deadline.Token);
            await tool.Input.WriteAsync(new byte[] { 1 }, deadline.Token);
            tool.Input.Dispose();
            await Task.WhenAll(first, second);
            await AssertExitAsync(tool, 37, deadline.Token);
            await tool.WaitAsync(deadline.Token);
            await AssertExitAsync(tool, 37, deadline.Token);
        }
        finally { if (tool is not null) { await tool.TerminateAndWaitAsync(); tool.Dispose(); } Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task TerminateAndWaitAsync_LiveDirectTool_JoinsWithoutSignalingSameGroupSibling()
    {
        if (AssertUnsupportedPlatform())
            return;
        string root = Directory.CreateTempSubdirectory("romd-tool-stop-").FullName;
        PosixMediaChild? tool = null, sibling = null;
        try
        {
            using var owner = NativeFileLease.TryAcquire(Path.Combine(root, "processing.lease")).ShouldNotBeNull();
            owner.Bind(Guid.NewGuid().ToString("N"));
            tool = PosixMediaChild.StartTool(fixture.Start("hold"), owner.Descriptor, []);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var first = await ReadReportAsync(tool, deadline.Token);
            sibling = PosixMediaChild.StartTool(fixture.Start("hold"), owner.Descriptor, []);
            var second = await ReadReportAsync(sibling, deadline.Token);
            first.GroupId.ShouldBe(second.GroupId);
            await tool.TerminateAndWaitAsync(deadline.Token);
            tool.HasExited.ShouldBeTrue();
            tool.ExitCode.ShouldBe(137);
            MediaProcessIdentity.Observe(tool.Id).Presence.ShouldBe(MediaProcessPresence.Gone);
            sibling.HasExited.ShouldBeFalse();
            await sibling.Input.WriteAsync(new byte[] { 1 }, deadline.Token);
            sibling.Input.Dispose();
            await sibling.WaitAsync(deadline.Token);
            await AssertExitAsync(sibling, 37, deadline.Token);
        }
        finally
        {
            if (tool is not null)
            { await tool.TerminateAndWaitAsync(); tool.Dispose(); }
            if (sibling is not null)
            { await sibling.TerminateAndWaitAsync(); sibling.Dispose(); }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TerminateAndWaitAsync_AlreadyJoined_IsIdempotentAndPreservesStatus()
    {
        if (AssertUnsupportedPlatform())
            return;
        string root = Directory.CreateTempSubdirectory("romd-tool-joined-").FullName;
        PosixMediaChild? tool = null;
        try
        {
            using var owner = NativeFileLease.TryAcquire(Path.Combine(root, "processing.lease")).ShouldNotBeNull();
            owner.Bind(Guid.NewGuid().ToString("N"));
            tool = PosixMediaChild.StartTool(fixture.Start("report"), owner.Descriptor, []);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await ReadReportAsync(tool, deadline.Token);
            await tool.WaitAsync(deadline.Token);
            await tool.TerminateAndWaitAsync(deadline.Token);
            await tool.TerminateAndWaitAsync(deadline.Token);
            await AssertExitAsync(tool, 37, deadline.Token);
            MediaProcessIdentity.Observe(tool.Id).Presence.ShouldBe(MediaProcessPresence.Gone);
        }
        finally { if (tool is not null) { await tool.TerminateAndWaitAsync(); tool.Dispose(); } Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task StartTool_StreamConstructionFails_KillsReapsAndClosesTransferredDescriptors(int failAt)
    {
        if (AssertUnsupportedPlatform())
            return;
        if (!NativeLeaseProcessFixture.IsChild)
        {
            using var isolated = new NativeLeaseProcessFixture();
            await isolated.RunAsync(nameof(StartTool_StreamConstructionFails_KillsReapsAndClosesTransferredDescriptors), failAt.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return;
        }
        string root = Directory.CreateTempSubdirectory("romd-tool-streams-").FullName;
        var streams = new List<FileStream>();
        var handles = new List<SafeFileHandle>();
        int pid = 0;
        try
        {
            string marker = Path.Combine(root, "pid");
            using var owner = NativeFileLease.TryAcquire(Path.Combine(root, "processing.lease")).ShouldNotBeNull();
            var failure = new IOException("injected tool stream construction failure");
            int calls = 0;
            var caught = Should.Throw<IOException>(() => PosixMediaChild.StartTool(fixture.Start("stream-failure", marker), owner.Descriptor, [], (descriptor, access) =>
            {
                handles.Add(descriptor);
                long began = Stopwatch.GetTimestamp();
                while (!File.Exists(marker) || !int.TryParse(File.ReadAllText(marker), NumberStyles.None, CultureInfo.InvariantCulture, out pid))
                {
                    if (Stopwatch.GetElapsedTime(began) > TimeSpan.FromSeconds(15))
                        throw new TimeoutException("The exact tool did not publish its PID.");
                    Thread.Sleep(10);
                }
                if (++calls == failAt)
                    throw failure;
                var stream = new FileStream(descriptor, access, 4096, isAsync: false);
                streams.Add(stream);
                return stream;
            }));
            caught.ShouldBeSameAs(failure);
            pid.ShouldBeGreaterThan(1);
            MediaProcessIdentity.Observe(pid).Presence.ShouldBe(MediaProcessPresence.Gone, "failed setup must finish native wait before returning");
            streams.Count.ShouldBe(failAt - 1);
            handles.Count.ShouldBe(failAt);
            foreach (var handle in handles)
                handle.IsClosed.ShouldBeTrue();
            using (var denied = NativeFileLease.TryAcquire(Path.Combine(root, "processing.lease")))
                denied.ShouldBeNull();
            owner.Dispose();
            using var reacquired = NativeFileLease.TryAcquire(Path.Combine(root, "processing.lease")).ShouldNotBeNull();
        }
        finally { foreach (var stream in streams) stream.Dispose(); Directory.Delete(root, recursive: true); }
    }

    private static bool AssertUnsupportedPlatform()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        Should.Throw<PlatformNotSupportedException>(() => PosixMediaChild.StartTool(new ProcessStartInfo("tool"), 0, []));
        return true;
    }

    private static async Task AssertExitAsync(PosixMediaChild tool, int expected, CancellationToken ct)
    {
        using var errors = new StreamReader(tool.Error, leaveOpen: true);
        char[] diagnostic = new char[4096];
        int count = await errors.ReadBlockAsync(diagnostic.AsMemory(), ct);
        tool.ExitCode.ShouldBe(expected, new string(diagnostic, 0, count));
    }

    private static async Task<ToolReport> ReadReportAsync(PosixMediaChild tool, CancellationToken ct)
    {
        using var reader = new StreamReader(tool.Output, leaveOpen: true);
        string? line = await reader.ReadLineAsync(ct);
        if (line is null)
        {
            await tool.WaitAsync(ct);
            using var errors = new StreamReader(tool.Error, leaveOpen: true);
            char[] diagnostic = new char[4096];
            int count = await errors.ReadBlockAsync(diagnostic.AsMemory(), ct);
            throw new InvalidOperationException($"Tool {tool.Id} reached stdout EOF before a report; joined exit code {tool.ExitCode}; stderr: {new string(diagnostic, 0, count)}");
        }
        return JsonSerializer.Deserialize<ToolReport>(line).ShouldNotBeNull();
    }

    private sealed record ToolReport(int Pid, int GroupId, int LeaseFlags, int StatusFlags, string Nonce, string[] Arguments, Dictionary<string, string?> Environment);
}
