using Romd.Admin.Application.Titles.Commands.MergeTitles;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles;

public sealed partial class MergeTitlesCommandHandlerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_UnknownProviders_UsesActualSeparateLayerClockFactsAndSkipsDuplicateReads(bool sameInstant)
    {
        var created = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567);
        var first = created.AddSeconds(1);
        var second = sameInstant ? first : created.AddSeconds(2);
        var clock = new MergeSequenceClock(created, first, second);
        var source = Title.Rehydrate(101, 7, "Source", "source", null, null, null, null, null, null, null,
            EnrichmentStatus.None, null, DateTimeOffset.UnixEpoch, metadataLayers:
            [
                TitleMetadataLayer.Rehydrate(1, 101, "UnlistedOne", MetadataSourceType.Provider,
                    """{"publisher":"First"}""", created.AddDays(10)),
                TitleMetadataLayer.Rehydrate(2, 101, "unlistedone", MetadataSourceType.Provider,
                    """{"publisher":"Duplicate"}""", created),
                TitleMetadataLayer.Rehydrate(3, 101, "UnlistedTwo", MetadataSourceType.Provider,
                    """{"publisher":"Second"}""", DateTimeOffset.UnixEpoch)
            ]);
        var target = NewTitle(202, 7, "Target");
        var (handler, _, _) = CreateHandler(source, target, providedClock: clock);

        var result = await handler.HandleAsync(new MergeTitlesCommand(101, 202, Current, Guid.Empty.ToString()));

        result.IsError.ShouldBeFalse();
        var imported = target.MetadataLayers.ToArray();
        imported.Select(layer => layer.SourceId).ShouldBe(["UnlistedOne", "UnlistedTwo"]);
        imported[0].UpdatedAt.ShouldBe(first);
        imported[1].UpdatedAt.ShouldBe(second);
        target.Publisher.ShouldBe(sameInstant ? "First" : "Second");
        clock.Reads.ShouldBe(3);
    }

    private sealed class MergeSequenceClock(params DateTimeOffset[] values) : TimeProvider
    {
        private readonly Queue<DateTimeOffset> _values = new(values);
        public int Reads { get; private set; }
        public override DateTimeOffset GetUtcNow()
        {
            Reads++;
            return _values.Dequeue();
        }
    }
}
