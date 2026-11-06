using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Content;
using Romd.Consumer.Application.Delivery;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class CatalogCdLayoutBoundaryTests
{
    [Theory]
    [InlineData("current")]
    [InlineData("binding")]
    [InlineData("revision")]
    [InlineData("release")]
    [InlineData("catalog-file")]
    [InlineData("representation")]
    [InlineData("member")]
    [InlineData("file")]
    [InlineData("path")]
    [InlineData("sha256")]
    [InlineData("size")]
    [InlineData("sha1")]
    [InlineData("md5")]
    [InlineData("crc")]
    [InlineData("generator")]
    [InlineData("invalid-shape")]
    public async Task CapturedDelivery_ExactScopedIdentity_RejectsEverySwappedFact(string change)
    {
        await RetainAsync(preferred: true, ObservedContainer(canonical: false));
        var captured = await CaptureDerivedAsync();
        var changed = change switch
        {
            "binding" => captured with { Id = captured.Id + 1 },
            "revision" => captured with { Revision = Guid.NewGuid() },
            "release" => captured with { ReleaseId = captured.ReleaseId + 1 },
            "catalog-file" => captured with { CatalogFileId = captured.CatalogFileId + 1 },
            "representation" => captured with { RepresentationId = captured.RepresentationId + 1 },
            "member" => captured with { MemberOrdinal = captured.MemberOrdinal + 1 },
            "file" => captured with { FileId = captured.FileId + 1 },
            "path" => captured with { RelativePath = "other.cue" },
            "sha256" => captured with { Identity = new(Cue.Size, Sha256.Parse(new string('f', 64)), Cue.Sha1, Cue.Md5, Cue.Crc32) },
            "size" => captured with { Identity = new(Cue.Size + 1, Cue.Sha256, Cue.Sha1, Cue.Md5, Cue.Crc32) },
            "sha1" => captured with { Identity = new(Cue.Size, Cue.Sha256, Sha1.Parse(new string('f', 40)), Cue.Md5, Cue.Crc32) },
            "md5" => captured with { Identity = new(Cue.Size, Cue.Sha256, Cue.Sha1, Md5.Parse(new string('f', 32)), Cue.Crc32) },
            "crc" => captured with { Identity = new(Cue.Size, Cue.Sha256, Cue.Sha1, Cue.Md5, Crc32.FromUInt32(47)) },
            "generator" => captured with { GeneratorProfile = "other-generator/v1" },
            "invalid-shape" => captured with { Revision = Guid.Empty },
            _ => captured
        };
        (await new ConsumerDerivedDescriptorSource(database.CreateOptions()).IsRetainedAsync(changed)).ShouldBe(change == "current");
    }

    [Theory]
    [InlineData("release-fingerprint")]
    [InlineData("requirement-fingerprint")]
    [InlineData("cue-digest")]
    [InlineData("bin-digest")]
    [InlineData("bin-name")]
    [InlineData("extra-sidecar")]
    [InlineData("title-conflict")]
    [InlineData("disk-requirement")]
    public async Task CapturedDelivery_CommittedCatalogDrift_RevokesWithoutObservingUncommittedChanges(string mutation)
    {
        await RetainAsync(preferred: true, ObservedContainer(canonical: false));
        var captured = await CaptureDerivedAsync();
        var sources = new ConsumerDerivedDescriptorSource(database.CreateOptions());
        await using var db = database.CreateContext();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await MutateAsync(db, mutation);
            (await sources.IsRetainedAsync(captured)).ShouldBeTrue();
            await transaction.CommitAsync();
        }
        (await sources.IsRetainedAsync(captured)).ShouldBeFalse();
    }

    [Fact]
    public async Task CapturedDelivery_LaterOriginalChangesNewSelection_RetainsTheCapturedDerivedIdentity()
    {
        await RetainAsync(preferred: true, ObservedContainer(canonical: false));
        var captured = await CaptureDerivedAsync();
        await using var db = database.CreateContext();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.Files.Add(new() { Id = 3, Sha256 = ExpectedCue.Sha256, Size = ExpectedCue.Size, SizeOnDisk = ExpectedCue.Size });
            await db.SaveChangesAsync();
            var proof = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), ExpectedCue.Sha256, ExpectedCue.Size, ExpectedCue, RomContainerFacts.Identity.Instance,
                new RomDecoderIdentity.Managed("fixture-hasher"), "identity/v1", "fixture-verifier", Observed, null, null);
            await db.CreatePreferredMeasuredWholeAsync("disc.cue", 3, RomContainerFacts.Identity.Instance, proof, Guid.Empty, Observed, CancellationToken.None);
            await transaction.CommitAsync();
        }
        await using (var snapshot = await db.Database.BeginTransactionAsync())
        {
            (await new DerivedDescriptorReader(db).ReadForReleaseAsync(1, CancellationToken.None)).ShouldBeEmpty();
            await snapshot.CommitAsync();
        }
        (await new ConsumerDerivedDescriptorSource(database.CreateOptions()).IsRetainedAsync(captured)).ShouldBeTrue();
        captured.Identity.ShouldBe(Cue);
        captured.Identity.ShouldNotBe(ExpectedCue);
    }

    private async Task<DerivedDescriptorBinding> CaptureDerivedAsync()
    {
        await using var db = database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var source = (await new DerivedDescriptorReader(db).ReadForReleaseAsync(1, CancellationToken.None)).ShouldHaveSingleItem();
        await transaction.CommitAsync();
        return source;
    }
}
