using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Romd.Admin.Application.MetadataProviders;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Persistence.Entities;
using Romd.Persistence.MetadataProviders;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Artwork;

public sealed class ProviderMatchPersistenceTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private readonly IProviderIdentityAdapter _adapter = Substitute.For<IProviderIdentityAdapter>();

    public ProviderMatchPersistenceTests()
    {
        _adapter.Id.Returns("test");
        _adapter.Name.Returns("Test provider");
        _adapter.Capabilities.Returns(new[] { "artwork", "search", "resolve" });
        _adapter.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(true, true));
        _adapter.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            Task.FromResult<ErrorOr<ProviderGameDto>>(new ProviderGameDto(call.Arg<string>(), "Game", "https://example.com/game")));
    }

    [Fact]
    public async Task Linking_ValidatesIdentityAndRejectsStaleRevision()
    {
        await Seed();
        await using var db = _database.CreateContext();
        var service = new TitleProviderMatchService(db, [_adapter]);
        var revision = (await service.GetAsync(1, default)).Value.Single().Revision;
        (await service.SetAsync(1, "test", new(revision, "42"), default)).IsError.ShouldBeFalse();
        db.ChangeTracker.Clear();
        (await service.SetAsync(1, "test", new(revision, "43"), default)).FirstError.Type.ShouldBe(ErrorType.Conflict);
        db.ChangeTracker.Clear();
        (await db.TitleExternalIds.SingleAsync()).ExternalId.ShouldBe("42");
        (await db.TitleMetadataLayers.CountAsync()).ShouldBe(0);
        (await db.ArtworkAssets.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Unlink_PreservesDataAndSuppressesRematching()
    {
        await Seed();
        await using var db = _database.CreateContext();
        var service = new TitleProviderMatchService(db, [_adapter]);
        var initial = (await service.GetAsync(1, default)).Value.Single();
        (await service.SetAsync(1, "test", new(initial.Revision, "42"), default)).IsError.ShouldBeFalse();
        db.ChangeTracker.Clear();
        var linked = (await service.GetAsync(1, default)).Value.Single();
        (await service.UnlinkAsync(1, "test", linked.Revision, default)).IsError.ShouldBeFalse();
        db.ChangeTracker.Clear();
        (await service.IsSuppressedAsync(1, "test", default)).ShouldBeTrue();
        (await service.GetLinkRevisionAsync(1, "test", "42", default)).ShouldBeNull();
        var unlinked = (await service.GetAsync(1, default)).Value.Single();
        (await service.SetAsync(1, "test", new(unlinked.Revision, "43"), default)).IsError.ShouldBeFalse();
        db.ChangeTracker.Clear();
        (await service.IsSuppressedAsync(1, "test", default)).ShouldBeFalse();
    }

    [Fact]
    public async Task LowConfidenceMatch_RequiresReviewBeforeArtworkBrowsing()
    {
        await Seed();
        await using var db = _database.CreateContext();
        db.TitleExternalIds.Add(TitleExternalIdEntity.FromDomain(Romd.Domain.Catalog.TitleExternalId.CreateNew(1, "test", "42", 0.2f)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = new TitleProviderMatchService(db, [_adapter]);
        var row = (await service.GetAsync(1, default)).Value.Single();
        row.State.ShouldBe("NeedsReview");
        (await service.GetLinkRevisionAsync(1, "test", "42", default)).ShouldBeNull();
        (await service.ConfirmAsync(1, "test", row.Revision, default)).IsError.ShouldBeFalse();
        db.ChangeTracker.Clear();
        (await service.GetLinkRevisionAsync(1, "test", "42", default)).ShouldNotBeNull();
    }

    [Fact]
    public async Task DisabledProvider_IsHiddenWithoutRemovingLink()
    {
        await Seed();
        await using var db = _database.CreateContext();
        var service = new TitleProviderMatchService(db, [_adapter]);
        var initial = (await service.GetAsync(1, default)).Value.Single();
        (await service.SetAsync(1, "test", new(initial.Revision, "42"), default)).IsError.ShouldBeFalse();
        _adapter.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(false, true));
        (await service.GetAsync(1, default)).Value.ShouldBeEmpty();
        (await db.TitleExternalIds.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task InvalidIdentity_DoesNotCreateAssociation()
    {
        await Seed();
        await using var db = _database.CreateContext();
        var service = new TitleProviderMatchService(db, [_adapter]);
        var initial = (await service.GetAsync(1, default)).Value.Single();
        _adapter.ResolveAsync("bad", Arg.Any<CancellationToken>()).Returns(Task.FromResult<ErrorOr<ProviderGameDto>>(ProviderMatchErrors.NotFound));
        (await service.SetAsync(1, "test", new(initial.Revision, "bad"), default)).IsError.ShouldBeTrue();
        (await db.TitleExternalIds.CountAsync()).ShouldBe(0);
    }

    private async Task Seed()
    {
        await using var db = _database.CreateContext();
        db.Platforms.Add(new PlatformEntity { Id = 1, Name = "Test", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Test", BaseCompactLabel = "Test", CanonicalKey = "test", ShortName = "test", Manufacturer = "Test", CreatedAt = DateTimeOffset.UtcNow });
        db.Titles.Add(new TitleEntity { Id = 1, PlatformId = 1, Name = "Game", NormalizedName = "game", EnrichmentStatus = "None",
            FieldProvenanceJson = "{}", FieldSourceOverridesJson = "{}", ScreenshotPrefsJson = "{}", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    public void Dispose() => _database.Dispose();
}
