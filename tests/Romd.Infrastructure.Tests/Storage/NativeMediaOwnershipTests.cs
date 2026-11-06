using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Romd.Infrastructure.Storage;
using Romd.Media.Processes;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class NativeMediaOwnershipTests
{
    [Fact]
    public void OpenRegularRead_OrdinaryFile_HoldsExactBytesAndCloseOnExecHandle()
    {
        if (OperatingSystem.IsWindows())
        {
            Should.Throw<PlatformNotSupportedException>(() => NativeFileLease.OpenRegularRead("unused"));
            return;
        }
        string root = Directory.CreateTempSubdirectory("romd-regular-read-").FullName;
        try
        {
            string path = Path.Combine(root, "input");
            byte[] expected = "held source bytes"u8.ToArray();
            File.WriteAllBytes(path, expected);
            using var handle = NativeFileLease.OpenRegularRead(path);
            NativeFileLease.Fcntl(checked((int)handle.DangerousGetHandle()), 1, 0).ShouldBe(1);
            byte[] actual = new byte[expected.Length];
            RandomAccess.Read(handle, actual, 0).ShouldBe(expected.Length);
            actual.ShouldBe(expected);
            handle.Dispose();
            handle.IsClosed.ShouldBeTrue();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenRegularRead_DirectoryOrLeafLink_RejectsBeforeReading(bool link)
    {
        if (OperatingSystem.IsWindows())
        {
            Should.Throw<PlatformNotSupportedException>(() => NativeFileLease.OpenRegularRead("unused"));
            return;
        }
        string root = Directory.CreateTempSubdirectory("romd-regular-kind-").FullName;
        try
        {
            string path = root;
            if (link)
            {
                string source = Path.Combine(root, "source");
                File.WriteAllBytes(source, "ordinary bytes"u8.ToArray());
                path = Path.Combine(root, "linked");
                File.CreateSymbolicLink(path, source);
            }
            Should.Throw<InvalidDataException>(() => { using var rejected = NativeFileLease.OpenRegularRead(path); });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Identity_ReplacedLeasePath_StillNamesOriginalFdAndRejectsReplacement()
    {
        if (OperatingSystem.IsWindows())
            return;
        string root = Directory.CreateTempSubdirectory("romd-admission-inode-").FullName;
        try
        {
            string path = Path.Combine(root, "processing.lease");
            using var original = NativeFileLease.TryAcquire(path).ShouldNotBeNull();
            var identity = original.Identity;
            identity.Inode.ShouldBeGreaterThan(0UL);
            File.Move(path, path + ".original");
            using var replacement = NativeFileLease.TryAcquire(path).ShouldNotBeNull();
            original.Identity.ShouldBe(identity, "the held description remains bound to its original inode after rename");
            replacement.Identity.ShouldNotBe(identity, "a new file at the same path cannot establish old custody extinction");
            using var blocked = NativeFileLease.TryAcquire(path + ".original");
            blocked.ShouldBeNull("a distinct description still conflicts with the live original owner");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TryAcquire_CreatesPrivateCloseOnExecLease()
    {
        if (OperatingSystem.IsWindows())
            return;
        string root = Directory.CreateTempSubdirectory("romd-native-lease-mode-").FullName;
        try
        {
            string path = Path.Combine(root, "processing.lease");
            using var owner = NativeFileLease.TryAcquire(path).ShouldNotBeNull();
            File.GetUnixFileMode(path).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                "the native optional create-mode argument must reach open without granting group or other access");
            NativeFileLease.Fcntl(owner.Descriptor, 1, 0).ShouldBe(1,
                "only explicitly duplicated admission descriptors may survive exec");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Fcntl_DuplicatesAboveFixedChildTargetsWithCloseOnExec()
    {
        if (OperatingSystem.IsWindows())
            return;
        string root = Directory.CreateTempSubdirectory("romd-native-lease-dup-").FullName;
        int duplicate = -1;
        try
        {
            string path = Path.Combine(root, "processing.lease");
            File.WriteAllBytes(path, []);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using var owner = NativeFileLease.TryAcquire(path).ShouldNotBeNull();
            duplicate = NativeFileLease.Fcntl(owner.Descriptor, OperatingSystem.IsMacOS() ? 67 : 1030, 10);
            duplicate.ShouldBeGreaterThanOrEqualTo(10, "fcntl must receive the optional minimum descriptor argument");
            NativeFileLease.Fcntl(duplicate, 1, 0).ShouldBe(1, "the clone must have FD_CLOEXEC before explicitly whitelisting child targets");
        }
        finally
        {
            if (duplicate >= 0)
                NativeFileLease.Close(duplicate);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Dispose_LeaseUsesDescriptorZero_ClosesOwnershipAndPermitsIndependentReacquisition()
    {
        if (OperatingSystem.IsWindows())
            return; // flock/native spawn is a POSIX boundary.
        string root = Directory.CreateTempSubdirectory("romd-zero-lease-").FullName;
        try
        {
            using var fixture = new MediaRuntimeParentFixture();
            using var process = fixture.Start(root, string.Empty, string.Empty, "lease-zero", false);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(deadline.Token);
            process.ExitCode.ShouldBe(0, "descriptor zero is a valid independently owned flock lease and must be closed");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Start_StreamConstructionFails_DisposesEveryTransferredDescriptorExactlyOnce(int failAt)
    {
        if (OperatingSystem.IsWindows())
            return; // Windows retains its named-job process owner.
        string root = Directory.CreateTempSubdirectory("romd-native-stream-").FullName;
        var opened = new List<FileStream>();
        var handles = new List<SafeFileHandle>();
        try
        {
            using var lease = NativeFileLease.TryAcquire(Path.Combine(root, "processing.lease")).ShouldNotBeNull();
            var start = new ProcessStartInfo("/bin/sleep") { UseShellExecute = false };
            start.ArgumentList.Add("30");
            start.Environment.Clear();
            int calls = 0;
            var failure = new IOException("injected pipe-stream construction failure");
            var caught = Should.Throw<IOException>(() => PosixMediaChild.Start(start, lease, (descriptor, access) =>
            {
                handles.Add(descriptor);
                if (++calls == failAt)
                    throw failure;
                var stream = new FileStream(descriptor, access, 4096, isAsync: false);
                opened.Add(stream);
                return stream;
            }));
            caught.ShouldBeSameAs(failure);
            opened.Count.ShouldBe(failAt - 1);
            handles.Count.ShouldBe(failAt);
            foreach (var handle in handles)
                handle.IsClosed.ShouldBeTrue("failed construction must dispose stream ownership before raw descriptors can be reused");
        }
        finally
        {
            foreach (var stream in opened)
                stream.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }
}
