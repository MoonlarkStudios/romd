using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Queries.GetTitleEnrichmentState;
using Romd.Application.Common.Ids;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles;

public sealed class GetTitleEnrichmentStateQueryHandlerTests
{
    private readonly ITitleAggregateReader _titles = Substitute.For<ITitleAggregateReader>();

    [Fact]
    public async Task HandleAsync_CompleteSnapshot_PreservesStateAndValidator()
    {
        var title = Title.Rehydrate(7, 1, "Game", "game", null, null, null, null, null, null, null,
            EnrichmentStatus.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            externalIds: [TitleExternalId.CreateNew(7, "igdb", "42", .9f, createdAt: DateTimeOffset.UnixEpoch)],
            metadataLayers: [TitleMetadataLayer.CreateNew(7, "igdb", MetadataSourceType.Provider,
                new() { Description = "Provider description", Players = 2 }, updatedAt: DateTimeOffset.UnixEpoch)],
            fieldProvenance: new() { ["Description"] = "igdb" }, revision: Guid.NewGuid());
        _titles.GetAggregateAsync(7, Arg.Any<CancellationToken>()).Returns(title);

        var result = await Create().HandleAsync(new(7));

        result.IsError.ShouldBeFalse();
        result.Value.TitleId.ShouldBe(IdCoder.Encode(7));
        result.Value.Revision.ShouldBe(title.Revision.ToString());
        result.Value.EnrichmentStatus.ShouldBe("Completed");
        result.Value.Layers.ShouldHaveSingleItem().Description.ShouldBe("Provider description");
        result.Value.ExternalIds.ShouldHaveSingleItem().ExternalId.ShouldBe("42");
        result.Value.FieldProvenance!["Description"].ShouldBe("igdb");
        result.Value.LastEnrichedAt.ShouldBe(title.LastEnrichedAt);
    }

    [Fact]
    public async Task HandleAsync_ReadRetriesExhausted_ReturnsSanitizedReadConflict()
    {
        _titles.GetAggregateAsync(7, Arg.Any<CancellationToken>()).Returns(Task.FromException<Title?>(Conflict()));

        var result = await Create().HandleAsync(new(7));

        result.FirstError.Type.ShouldBe(ErrorType.Conflict);
        result.FirstError.Code.ShouldBe("Titles.ReadConflict");
        result.FirstError.Description.ShouldNotContain("private detail");
    }

    [Fact]
    public async Task HandleAsync_MissingTitle_ReturnsNotFound()
    {
        var result = await Create().HandleAsync(new(7));
        result.FirstError.Code.ShouldBe("Catalog.TitleNotFound");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_CancellationDuringRead_PreservesCancellation(bool conflictRacesCancellation)
    {
        using var cancellation = new CancellationTokenSource();
        _titles.GetAggregateAsync(7, cancellation.Token).Returns(_ =>
        {
            cancellation.Cancel();
            return conflictRacesCancellation ? Task.FromException<Title?>(Conflict()) : Task.FromCanceled<Title?>(cancellation.Token);
        });

        var error = await Should.ThrowAsync<OperationCanceledException>(() => Create().HandleAsync(new(7), cancellation.Token));

        error.CancellationToken.ShouldBe(cancellation.Token);
    }

    [Fact]
    public async Task HandleAsync_UnexpectedFailure_StillPropagates()
    {
        var failure = new IOException("storage failure");
        _titles.GetAggregateAsync(7, Arg.Any<CancellationToken>()).Returns(Task.FromException<Title?>(failure));
        (await Should.ThrowAsync<IOException>(() => Create().HandleAsync(new(7)))).ShouldBeSameAs(failure);
    }

    private GetTitleEnrichmentStateQueryHandler Create() => new(_titles);
    private static PersistenceConflictException Conflict() => new(new InvalidOperationException("private detail"));
}
