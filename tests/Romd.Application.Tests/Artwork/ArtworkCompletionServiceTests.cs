using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Artwork.Commands.QueueArtworkCompletion;
using Romd.Admin.Application.Common.Persistence;
using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Contracts.Management.Artwork;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Artwork;

public sealed class ArtworkCompletionServiceTests
{
    private readonly IArtworkCompletionStore store = Substitute.For<IArtworkCompletionStore>();
    private readonly IArtworkCompletionAcceptance acceptance = Substitute.For<IArtworkCompletionAcceptance>();
    private readonly IUnitOfWork work = Substitute.For<IUnitOfWork>();
    private Task<ErrorOr<Guid>> QueueAsync(ArtworkCompletionRequest request) =>
        new QueueArtworkCompletionCommandHandler(acceptance, work, TimeProvider.System,
            NullLogger<QueueArtworkCompletionCommandHandler>.Instance).HandleAsync(new(request, Guid.NewGuid()));

    public static TheoryData<ArtworkCompletionFilterDto> InvalidEnrichmentFilters => new()
    {
        new(Enrichment: []),
        new(Enrichment: [""]),
        new(Enrichment: [" "]),
        new(Enrichment: [null!]),
        new(Enrichment: ["needsreview"]),
        new(Enrichment: ["NeedsReview,Blocked"]),
        new(Enrichment: ["Settled", "Automatic"]),
        new(EnrichmentStatus: "NeedsReview"),
        new(EnrichmentStatus: "Automatic"),
        new(EnrichmentStatus: "Processing"),
        new(EnrichmentStatus: "Blocked"),
        new(EnrichmentStatus: "1"),
        new(EnrichmentStatus: "pending"),
        new(EnrichmentStatus: "Pending, Completed")
    };

    public static TheoryData<ArtworkCompletionFilterDto> ValidEnrichmentFilters => new()
    {
        new(),
        new(Enrichment: ["NeedsReview", "Blocked", "NeedsReview"]),
        new(Enrichment: ["Settled", "Settled"]),
        new(EnrichmentStatus: "LowConfidence", Enrichment: ["Processing"]),
        new(EnrichmentStatus: " ")
    };

    [Theory]
    [MemberData(nameof(InvalidEnrichmentFilters))]
    public async Task PreviewAndQueueScope_InvalidEnrichmentFilter_ReturnInvalidRequestWithoutReachingStore(ArtworkCompletionFilterDto filters)
    {
        var service = new ArtworkCompletionService(store);

        foreach (var scope in new[] { "Filtered", "Automatic" })
        {
            var request = new ArtworkCompletionRequest(Guid.NewGuid(), Scope: scope, Filters: filters, Mode: "Enrich");
            (await service.PreviewScopeAsync(request, default)).FirstError.Code.ShouldBe(ArtworkCurationErrors.InvalidRequest().Code, scope);
            (await QueueAsync(request)).FirstError.Code.ShouldBe(ArtworkCurationErrors.InvalidRequest().Code, scope);
        }

        await store.DidNotReceiveWithAnyArgs().PreviewScopeAsync(default!, default);
        acceptance.ReceivedCalls().ShouldBeEmpty();
        work.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Unresolved")]
    [InlineData("NeedsAttention")]
    [InlineData("Ready")]
    [InlineData("NeedsMatch")]
    [InlineData("Updated")]
    [InlineData("1")]
    [InlineData("review")]
    [InlineData("")]
    public async Task Items_RetiredOrUnknownStatus_ReturnInvalidRequestWithoutReachingStore(string status)
    {
        var result = await new ArtworkCompletionService(store).ItemsAsync(Guid.NewGuid(), 0, status, default);

        result.FirstError.Code.ShouldBe(ArtworkCurationErrors.InvalidRequest().Code);
        await store.DidNotReceiveWithAnyArgs().ItemsAsync(default, default, default, default);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Review")]
    [InlineData("Failed")]
    [InlineData("Unfinished")]
    [InlineData("Working")]
    public async Task Items_RunFilter_PassesToStore(string? status)
    {
        var jobId = Guid.NewGuid();
        store.ItemsAsync(jobId, 0, status, Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From(new ArtworkCompletionItemsDto([], null)));

        (await new ArtworkCompletionService(store).ItemsAsync(jobId, 0, status, default)).IsError.ShouldBeFalse();
        await store.Received(1).ItemsAsync(jobId, 0, status, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Unresolved")]
    [InlineData("Review")]
    [InlineData("Working")]
    [InlineData("Updated")]
    [InlineData("4")]
    public async Task Retry_StatusOtherThanFailedOrUnfinished_ReturnsInvalidRequestWithoutReachingStore(string? status)
    {
        var service = new ArtworkCompletionService(store);
        var request = new ArtworkCompletionRequest(Guid.NewGuid(), Scope: "Retry", RetryJobId: Guid.NewGuid(), RetryStatus: status);

        (await service.PreviewScopeAsync(request, default)).FirstError.Code.ShouldBe(ArtworkCurationErrors.InvalidRequest().Code);
        (await QueueAsync(request)).FirstError.Code.ShouldBe(ArtworkCurationErrors.InvalidRequest().Code);
        await store.DidNotReceiveWithAnyArgs().PreviewScopeAsync(default!, default);
        acceptance.ReceivedCalls().ShouldBeEmpty();
        work.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Unfinished")]
    public async Task Retry_FailedOrUnfinished_ReachesStore(string status)
    {
        store.PreviewScopeAsync(Arg.Any<ArtworkCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(ErrorOrFactory.From(new ArtworkCompletionPreviewDto(0, [])));
        var request = new ArtworkCompletionRequest(Guid.NewGuid(), Scope: "Retry", RetryJobId: Guid.NewGuid(), RetryStatus: status);

        (await new ArtworkCompletionService(store).PreviewScopeAsync(request, default)).IsError.ShouldBeFalse();
        await store.Received(1).PreviewScopeAsync(request, Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(ValidEnrichmentFilters))]
    public async Task PreviewScope_ValidEnrichmentFilter_PassesRequestToStoreUnchanged(ArtworkCompletionFilterDto filters)
    {
        store.PreviewScopeAsync(Arg.Any<ArtworkCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(ErrorOrFactory.From(new ArtworkCompletionPreviewDto(0, [])));
        var service = new ArtworkCompletionService(store);

        foreach (var scope in new[] { "Filtered", "Automatic" })
        {
            var request = new ArtworkCompletionRequest(Guid.NewGuid(), Scope: scope, Filters: filters, Mode: "Enrich");
            (await service.PreviewScopeAsync(request, default)).IsError.ShouldBeFalse(scope);
            await store.Received(1).PreviewScopeAsync(request, Arg.Any<CancellationToken>());
        }
    }
}
