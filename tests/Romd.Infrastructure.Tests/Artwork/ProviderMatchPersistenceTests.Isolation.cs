using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Application.Common.Concurrency;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Domain.Catalog;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence.Entities;
using Romd.Persistence.MetadataProviders;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Artwork;

public sealed partial class ProviderMatchPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchReadsAndMutations_MalformedUnrelatedProvider_StayIsolated(bool registerDisabledProvider)
    {
        await Seed();
        await using var db = _database.CreateContext();
        var fingerprint = await db.Titles.Where(title => title.Id == 1)
            .Select(title => ProviderIdentityDatabaseFunctions.Fingerprint(title.Id, ProviderIdentityPolicy.Version)).SingleAsync();
        db.TitleExternalIds.Add(TitleExternalIdEntity.FromDomain(TitleExternalId.CreateNew(1, "test", "42", 1, createdAt: DateTimeOffset.UnixEpoch)));
        db.Set<TitleProviderMatchStateEntity>().AddRange(
            new TitleProviderMatchStateEntity
            {
                TitleId = 1,
                ProviderId = "test",
                Revision = Guid.NewGuid(),
                AutomaticallyTrusted = true,
                InputFingerprint = fingerprint,
                MatcherVersion = ProviderIdentityPolicy.Version,
                GameJson = JsonSerializer.Serialize(new ProviderGame("42", "Game", "https://example.com/game")),
                DecisionJson = JsonSerializer.Serialize(new ProviderIdentityDecision("AutoAccepted", "test", "test", fingerprint, []))
            },
            new TitleProviderMatchStateEntity
            {
                TitleId = 1,
                ProviderId = "broken",
                Revision = Guid.NewGuid(),
                GameJson = "invalid game JSON",
                DecisionJson = "invalid decision JSON"
            });
        db.TitleMetadataLayers.Add(new TitleMetadataLayerEntity
        {
            TitleId = 1,
            SourceId = "broken",
            SourceType = (int)MetadataSourceType.Provider,
            MetadataJson = "invalid metadata JSON"
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var disabled = Substitute.For<IProviderIdentityAdapter>();
        disabled.Id.Returns("broken");
        disabled.Name.Returns("Broken disabled provider");
        disabled.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(false, true));
        var identityStore = new ProviderIdentityStore(db, Substitute.For<ITitleEnrichmentEvidenceReader>(), TimeProvider.System);
        var service = ProviderMatchTestServices.Create(db, registerDisabledProvider ? [_adapter, disabled] : [_adapter],
            identityStore: identityStore);

        var single = (await service.GetAsync(1, "test", default)).Value;
        single.State.ShouldBe("AutoMatched");
        var listed = (await service.GetAsync(1, default)).Value.ShouldHaveSingleItem();
        (listed.ProviderId, listed.State, listed.Revision).ShouldBe((single.ProviderId, single.State, single.Revision));
        var confirmed = await service.ConfirmAsync(1, "test", RevisionPrecondition.For(single.Revision.ToString()), default);
        confirmed.IsError.ShouldBeFalse();
        confirmed.Value.State.ShouldBe("Confirmed");
        confirmed.Value.PendingJobId.ShouldNotBeNull();
        var unlinked = await service.UnlinkAsync(1, "test", RevisionPrecondition.For(confirmed.Value.Revision.ToString()), default);
        unlinked.IsError.ShouldBeFalse();
        unlinked.Value.State.ShouldBe("Unlinked");
        unlinked.Value.PendingJobId.ShouldBeNull();
        (await db.TitleExternalIds.CountAsync()).ShouldBe(0);
        (await db.Set<EnrichmentJobEntity>().CountAsync()).ShouldBe(1);
        var broken = await db.Set<TitleProviderMatchStateEntity>().SingleAsync(state => state.ProviderId == "broken");
        broken.GameJson.ShouldBe("invalid game JSON");
        broken.DecisionJson.ShouldBe("invalid decision JSON");
    }
}
