using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Content;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Hashing;
using Romd.Domain.Libraries;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class CatalogCdLayoutBoundaryTests
{
    private static readonly Guid ManifestUser = Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
    private async Task PublishConsumerLibraryAsync()
    {
        await using var db = database.CreateContext();
        db.Libraries.Add(new()
        {
            Id = 1,
            Name = "Fixture",
            ConfigurationJson = "{}",
            ConfigurationState = LibraryConfigurationState.Valid.ToString(),
            NeedsMaterialization = false,
            CreatedAt = Observed
        });
        db.Users.Add(new RomdUser { Id = ManifestUser, UserName = "fixture", NormalizedUserName = "FIXTURE", LibraryId = 1, CreatedAt = Observed });
        db.MaterializedLibraryReleases.Add(new MaterializedLibraryReleaseEntity
        {
            LibraryId = 1,
            TitleId = 1,
            CatalogReleaseId = 1,
            DatGameId = 1,
            DatFileId = 1,
            PlatformId = 1,
            IsEligible = true,
            IsComplete = true,
            IsOwned = true,
            IsPlayable = true,
            IsExposed = true,
            ExposureReason = "ExposedDefault"
        });
        await db.SaveChangesAsync();
    }
    private async Task<ConsumerReleaseManifest> ConsumerManifestAsync()
    {
        await using var db = database.CreateContext();
        var repository = new ConsumerReleaseManifestRepository(db, RomMediaPolicyComposition.CreateDeliveryPolicy(),
            MediaTestServices.CreateContentCatalog(db), new DerivedDescriptorReader(db), new ConsumerMediaAdmission("fixture-hasher", Decoder));
        var result = await repository.GetManifestAsync(new(new(ManifestUser), 1));
        return result.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerReleaseManifest>.Found>().Value;
    }

    [Fact]
    public async Task ConsumerManifest_DerivedDescriptor_CompletesDeliveryWithoutCompletingOriginalInventory()
    {
        await RetainAsync(preferred: true, ObservedContainer(canonical: false));
        await PublishConsumerLibraryAsync();
        var manifest = await ConsumerManifestAsync();
        manifest.IsComplete.ShouldBeFalse();
        manifest.IsDeliveryComplete.ShouldBeTrue();
        var cue = manifest.Items.Single(item => item.RelativePath == "disc.cue");
        var source = cue.Source.ShouldBeOfType<ConsumerReleaseManifestSource.Derived>();
        source.Descriptor.Identity.ShouldBe(Cue);
        source.Descriptor.Identity.ShouldNotBe(ExpectedCue);
        cue.ExpectedSizeBytes.ShouldBe(ExpectedCue.Size);
        cue.SizeBytes.ShouldBe(Cue.Size);
        cue.Sha256.ShouldBe(Cue.Sha256);
        cue.IsAvailable.ShouldBeTrue();
        cue.Formats.ShouldBeEmpty();
        manifest.Runtime.MinimumInstallBytes.ShouldBe(Bin.Size + Cue.Size);
        manifest.Runtime.Launch!.RelativePath.ShouldBe("disc.cue");
        await using var observer = database.CreateContext();
        (await CountAsync(observer, "Roms")).ShouldBe(1);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("sha1-only")]
    [InlineData("md5")]
    [InlineData("crc")]
    [InlineData("size")]
    public async Task ConsumerManifest_OriginalPriority_RequiresTheCompleteDatTuple(string match)
    {
        await RetainAsync(preferred: true, ObservedContainer(canonical: false));
        await PublishConsumerLibraryAsync();
        var captured = (await ConsumerManifestAsync()).Items.Single(item => item.RelativePath == "disc.cue").Source
            .ShouldBeOfType<ConsumerReleaseManifestSource.Derived>().Descriptor;
        var original = new RomLogicalIdentity(ExpectedCue.Size + (match == "size" ? 1 : 0), Sha256.Parse(new string('e', 64)), ExpectedCue.Sha1,
            match is "md5" or "sha1-only" ? Md5.Parse(new string('f', 32)) : ExpectedCue.Md5,
            match is "crc" or "sha1-only" ? Crc32.FromUInt32(47) : ExpectedCue.Crc32);
        await using (var db = database.CreateContext())
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.Files.Add(new() { Id = 3, Sha256 = original.Sha256, Size = original.Size, SizeOnDisk = original.Size });
            await db.SaveChangesAsync();
            var proof = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), original.Sha256, original.Size, original, RomContainerFacts.Identity.Instance,
                new RomDecoderIdentity.Managed("fixture-hasher"), "identity/v1", "fixture-verifier", Observed, null, null);
            await db.CreatePreferredMeasuredWholeAsync("disc.cue", 3, RomContainerFacts.Identity.Instance, proof, Guid.Empty, Observed, CancellationToken.None);
            await transaction.CommitAsync();
        }
        var next = await ConsumerManifestAsync();
        var cue = next.Items.Single(item => item.RelativePath == "disc.cue");
        next.IsDeliveryComplete.ShouldBeTrue();
        next.IsComplete.ShouldBe(match == "exact");
        if (match == "exact")
        {
            cue.Source.ShouldBeOfType<ConsumerReleaseManifestSource.Original>().Identity.ShouldBe(original);
            cue.Sha256.ShouldBe(original.Sha256);
            cue.SizeBytes.ShouldBe(original.Size);
        }
        else
            cue.Source.ShouldBeOfType<ConsumerReleaseManifestSource.Derived>().Descriptor.ShouldBe(captured);
        (await new ConsumerDerivedDescriptorSource(database.CreateOptions()).IsRetainedAsync(captured)).ShouldBeTrue();
    }

    [Fact]
    public async Task ConsumerManifest_CurrentCatalogDrift_RefusesDerivedFallbackAndDoesNotClaimPreservation()
    {
        await RetainAsync(preferred: true, ObservedContainer(canonical: false));
        await PublishConsumerLibraryAsync();
        (await ConsumerManifestAsync()).IsDeliveryComplete.ShouldBeTrue();
        await using (var db = database.CreateContext())
            await MutateAsync(db, "cue-digest");
        var next = await ConsumerManifestAsync();
        next.IsComplete.ShouldBeFalse();
        next.IsDeliveryComplete.ShouldBeFalse();
        var cue = next.Items.Single(item => item.RelativePath == "disc.cue");
        cue.Source.ShouldBeOfType<ConsumerReleaseManifestSource.Missing>();
        cue.Sha256.ShouldBeNull();
        cue.IsAvailable.ShouldBeFalse();
    }
}
