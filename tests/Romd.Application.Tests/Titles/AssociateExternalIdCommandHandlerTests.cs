using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.AssociateExternalId;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;
using Romd.Domain.Source.Platform;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles;

public sealed class AssociateExternalIdCommandHandlerTests
{
    [Fact]
    public async Task HandleAsync_ProviderReturnsContentRatingClaims_StoresClaimsInProviderLayer()
    {
        var title = Title.Rehydrate(
            id: 1,
            platformId: 10,
            name: "Super Mario World",
            normalizedName: "super mario world",
            description: null,
            publisher: null,
            developer: null,
            genre: null,
            releaseDate: null,
            players: null,
            rating: null,
            enrichmentStatus: EnrichmentStatus.None,
            lastEnrichedAt: null,
            createdAt: DateTimeOffset.UtcNow);
        var claims = new[]
        {
            new ContentRatingClaim
            {
                Board = RatingBoard.Esrb,
                RawCode = "T",
                ExternalRatingId = "1001",
                Descriptors = ["Fantasy Violence"],
                Synopsis = "ESRB synopsis"
            },
            new ContentRatingClaim
            {
                Board = RatingBoard.Pegi,
                RawCode = "12",
                ExternalRatingId = "1002",
                Descriptors = ["Violence"],
                Synopsis = "PEGI synopsis"
            }
        };
        var provider = new FakeMetadataProvider("igdb",
            EnrichmentResult.Found("12345", 1.0f, new EnrichmentData
            {
                ContentRatings = claims
            }));
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var transaction = Substitute.For<ITransaction>();
        unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        var titleRepository = Substitute.For<ITitleRepository>();
        var platformRepository = Substitute.For<IPlatformRepository>();
        var rematerializationScheduler = Substitute.For<IRematerializationScheduler>();

        titleRepository.GetWithMetadataLayersAsync(1, Arg.Any<CancellationToken>())
            .Returns(title);
        titleRepository.GetTitleDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(new TitleDetailData
            {
                Id = 1,
                PlatformId = 10,
                Name = "Super Mario World",
                EnrichmentStatus = "Completed",
                IsTracked = false,
                CreatedAt = DateTimeOffset.UtcNow
            });
        platformRepository.GetByIdAsync(10, Arg.Any<CancellationToken>())
            .Returns(Platform.Rehydrate(10, "Super Nintendo", "snes", "Nintendo", DateTimeOffset.UtcNow));

        var handler = new AssociateExternalIdCommandHandler(TestSystemCatalog.Create(),
            titleRepository,
            unitOfWork,
            platformRepository,
            [provider],
            rematerializationScheduler,
            NullLogger<AssociateExternalIdCommandHandler>.Instance);

        var result = await handler.HandleAsync(new AssociateExternalIdCommand(1, "igdb", "12345"));

        result.IsError.ShouldBeFalse();
        var layer = title.MetadataLayers.Single();
        layer.SourceId.ShouldBe("igdb");
        var payload = layer.GetPayload();
        payload.ShouldNotBeNull();
        payload.ContentRatings.ShouldNotBeNull();
        payload.ContentRatings.Count.ShouldBe(2);

        var esrb = payload.ContentRatings.Single(r => r.Board == RatingBoard.Esrb);
        esrb.RawCode.ShouldBe("T");
        esrb.ExternalRatingId.ShouldBe("1001");
        esrb.Descriptors.ShouldBe(["Fantasy Violence"]);
        esrb.Synopsis.ShouldBe("ESRB synopsis");

        var pegi = payload.ContentRatings.Single(r => r.Board == RatingBoard.Pegi);
        pegi.RawCode.ShouldBe("12");
        pegi.ExternalRatingId.ShouldBe("1002");
        pegi.Descriptors.ShouldBe(["Violence"]);
        pegi.Synopsis.ShouldBe("PEGI synopsis");

        await titleRepository.Received(1).UpdateAsync(title, Arg.Any<CancellationToken>());
        await rematerializationScheduler.Received(1).EnqueueTitleAsync(1, Arg.Any<CancellationToken>());
        Received.InOrder(() =>
        {
            unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>());
            rematerializationScheduler.EnqueueTitleAsync(1, Arg.Any<CancellationToken>());
            titleRepository.UpdateAsync(title, Arg.Any<CancellationToken>());
            transaction.CommitAsync(Arg.Any<CancellationToken>());
        });
    }

    private sealed class FakeMetadataProvider(string providerId, EnrichmentResult result) : IMetadataProvider
    {
        public string ProviderId { get; } = providerId;
        public string DisplayName => ProviderId;
        public bool IsConfigured => true;

        public async IAsyncEnumerable<(EnrichmentContext Context, EnrichmentResult Result)> EnrichAsync(
            IAsyncEnumerable<EnrichmentContext> contexts,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var context in contexts.WithCancellation(ct))
            {
                yield return (context, result);
            }
        }
    }
}
