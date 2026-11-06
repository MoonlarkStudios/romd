using Romd.Domain.Hashing;
using Romd.Domain.Source.Dat;
using Romd.Domain.Source.Platform;
using Romd.Domain.Source.Rom;
using Romd.Domain.Storage;
using DomainPlatform = Romd.Domain.Source.Platform.Platform;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Source;

public sealed class SourceCreationTimeTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(5.5)).AddTicks(1234567);
    private static readonly Sha256 ContentSha256 = Sha256.Parse(new string('a', 64));
    private static readonly Sha1 ContentSha1 = Sha1.Parse(new string('b', 40));
    private static readonly Md5 ContentMd5 = Md5.Parse(new string('c', 32));
    private static readonly Crc32 Crc = Crc32.Parse("12345678");

    [Fact]
    public void Factories_ExplicitInstant_RetainExactPrecisionAndOffset()
    {
        DateTimeOffset[] timestamps =
        [
            FileEntity.CreateNew(ContentSha256, 10, 8, true, Now).CreatedAt,
            DomainPlatform.CreateNew("Platform", "key", Now, "Manufacturer").CreatedAt,
            PlatformAlias.CreateName(1, " Alias ", Now).CreatedAt,
            PlatformAlias.CreateProviderMapping(1, "igdb", "12", Now).CreatedAt,
            DatSource.CreateNew(Now).CreatedAt,
            DatFile.CreateNew("Dat", "Description", DatType.Custom, "test.dat", 1, Now).ImportedAt,
            DatFile.CreatePendingVersion("Dat", "Description", DatType.Custom, "test.dat", 1, 2, Now).ImportedAt,
            RomFile.CreateNew("rom.bin", 1, 10, ContentSha256, ContentSha1, ContentMd5, Crc, Now).ImportedAt,
            RomFile.CreateRepresented("rom.iso", 1, 10, ContentSha256, ContentSha1, ContentMd5, Crc, RomRepresentationEncoding.Rvz, Now).ImportedAt
        ];

        foreach (var timestamp in timestamps)
        {
            timestamp.Ticks.ShouldBe(Now.Ticks);
            timestamp.Offset.ShouldBe(Now.Offset);
        }
    }

    [Fact]
    public void UpdateStatistics_ExplicitInstant_PreservesImportAndLifecycle()
    {
        var dat = DatFile.CreatePendingVersion("Dat", "Description", DatType.Custom, "test.dat", 1, 2, Now);
        var updated = Now.AddHours(3).ToOffset(TimeSpan.FromHours(-4));

        dat.UpdateStatistics(5, 8, 2, updated);

        dat.GameCount.ShouldBe(5);
        dat.RomCount.ShouldBe(8);
        dat.DiskCount.ShouldBe(2);
        dat.LastUpdatedAt.ShouldNotBeNull().Ticks.ShouldBe(updated.Ticks);
        dat.LastUpdatedAt.Value.Offset.ShouldBe(updated.Offset);
        dat.ImportedAt.Ticks.ShouldBe(Now.Ticks);
        dat.ImportedAt.Offset.ShouldBe(Now.Offset);
        dat.Lifecycle.ShouldBe(DatFileLifecycle.PendingActivation);
        dat.DatSourceId.ShouldBe(2);
    }

    [Fact]
    public void CreatePendingVersion_InvalidSource_RejectsBeforeCreatingIdentity()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            DatFile.CreatePendingVersion("Dat", "Description", DatType.Custom, "test.dat", 1, 0, Now));
    }
}
