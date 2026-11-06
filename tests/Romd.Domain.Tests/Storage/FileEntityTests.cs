using Romd.Domain.Hashing;
using Romd.Domain.Storage;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Storage;

public sealed class FileEntityTests
{
    [Fact]
    public void CreateNew_UnmeasuredLegacyArtifact_LeavesChecksumsUnknown()
    {
        var file = FileEntity.CreateNew(Sha256.Parse(new string('a', 64)), 100, 40, true, now: DateTimeOffset.UnixEpoch);

        file.ArtifactMd5.ShouldBeNull();
        file.ArtifactSha1.ShouldBeNull();
        file.Size.ShouldBe(100);
        file.SizeOnDisk.ShouldBe(40);
        file.BytesSaved.ShouldBe(60);
    }

    [Fact]
    public void Rehydrate_MeasuredArtifact_KeepsByteIdentityAndChecksumsSeparateFromCompression()
    {
        var sha256 = Sha256.Parse(new string('a', 64));
        var md5 = Md5.Parse(new string('b', 32));
        var sha1 = Sha1.Parse(new string('c', 40));
        var file = FileEntity.Rehydrate(7, sha256, 100, 40, true, DateTimeOffset.UtcNow, md5, sha1);

        file.Sha256.ShouldBe(sha256);
        file.ArtifactMd5.ShouldBe(md5);
        file.ArtifactSha1.ShouldBe(sha1);
        file.Size.ShouldBe(100);
        file.CompressionRatio.ShouldBe(0.4);
    }
}
