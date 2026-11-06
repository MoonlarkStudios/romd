using System.Security.Cryptography;
using Romd.Media.Processes;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests.Processes;

public sealed class SealedExecutableTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("romd-sealed-image-").FullName;

    [Fact]
    public void Seal_RegularFile_MeasuresTheSealedBytesAndRefusesEveryChange()
    {
        string path = Path.Combine(_directory, "tool");
        byte[] bytes = RandomNumberGenerator.GetBytes(200_000);
        File.WriteAllBytes(path, bytes);
        if (!OperatingSystem.IsLinux())
        {
            Should.Throw<PlatformNotSupportedException>(() => SealedExecutable.Seal(path, "tool", CancellationToken.None));
            return;
        }
        using var image = SealedExecutable.Seal(path, "tool", CancellationToken.None);
        image.Bytes.ShouldBe(bytes.Length);
        image.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        NativeFileLease.Fcntl(image.Descriptor, 1034, 0).ShouldBe(SealedExecutable.ImmutableSeals);
        NativeFileLease.Fcntl(image.Descriptor, 1, 0).ShouldBe(1, "the worker's own copy stays close-on-exec");

        // The source path may change afterwards; the sealed image does not.
        File.WriteAllBytes(path, new byte[bytes.Length]);
        Should.Throw<UnauthorizedAccessException>(() => RandomAccess.Write(image.Handle, new byte[] { 1 }, 0));
        Should.Throw<UnauthorizedAccessException>(() => RandomAccess.Write(image.Handle, new byte[] { 1 }, bytes.Length));
        RandomAccess.GetLength(image.Handle).ShouldBe(bytes.Length);
        byte[] sealedBytes = new byte[bytes.Length];
        RandomAccess.Read(image.Handle, sealedBytes, 0).ShouldBe(bytes.Length);
        sealedBytes.ShouldBe(bytes);
    }

    [Theory]
    [InlineData("link")]
    [InlineData("device")]
    [InlineData("empty")]
    [InlineData("oversized")]
    public void Seal_LinkSpecialEmptyOrOversizedSource_IsRefused(string kind)
    {
        if (!OperatingSystem.IsLinux())
        {
            Should.Throw<PlatformNotSupportedException>(() => SealedExecutable.Seal("/dev/null", "tool", CancellationToken.None));
            return;
        }
        string path = Path.Combine(_directory, kind);
        switch (kind)
        {
            case "link":
                File.WriteAllBytes(path + ".target", [1, 2, 3]);
                File.CreateSymbolicLink(path, path + ".target");
                break;
            case "device":
                path = "/dev/null";
                break;
            case "empty":
                File.WriteAllBytes(path, []);
                break;
            case "oversized":
                using (var file = File.Create(path))
                    file.SetLength(SealedExecutable.MaximumBytes + 1);
                break;
        }
        Should.Throw<InvalidDataException>(() => SealedExecutable.Seal(path, "tool", CancellationToken.None));
    }

    [Fact]
    public void Seal_Cancelled_ThrowsBeforeReturningAnImage()
    {
        string path = Path.Combine(_directory, "tool");
        File.WriteAllBytes(path, [1, 2, 3]);
        if (!OperatingSystem.IsLinux())
            Should.Throw<PlatformNotSupportedException>(() => SealedExecutable.Seal(path, "tool", new CancellationToken(true)));
        else
            Should.Throw<OperationCanceledException>(() => SealedExecutable.Seal(path, "tool", new CancellationToken(true)));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
