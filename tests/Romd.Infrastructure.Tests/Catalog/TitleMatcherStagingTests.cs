using NSubstitute;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Matching;
using Romd.Domain.Catalog;
using Romd.Infrastructure.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Catalog;

public sealed class TitleMatcherStagingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddTicks(1234567);

    [Fact]
    public async Task MatchOrStage_MixedNames_RetainsOrderAndCreatedPositionsUntilOwnerFlushes()
    {
        var fixture = new Fixture();

        var staged = await fixture.Matcher.MatchOrStageBatchAsync(7,
            ["Existing", "Alpha (USA)", "Beta", "Alpha (Europe)", "Existing"]);

        staged.RequiresFlush.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => staged.ReadAfterFlush());
        fixture.Pending.Select(title => title.Name).ShouldBe(["Alpha", "Beta"]);
        fixture.Pending.Select(title => title.CreatedAt).ShouldBe([Now, Now.AddTicks(1)]);
        fixture.Clock.Reads.ShouldBe(2);
        fixture.Receipt.Flushed = true;
        staged.RequiresFlush.ShouldBeFalse();
        var matches = staged.ReadAfterFlush();
        matches.Select(match => match.TitleId).ShouldBe([101, 201, 202, 201, 101]);
        matches.Select(match => match.TitleWasCreated).ShouldBe([false, true, true, false, false]);
        staged.ReadAfterFlush().ShouldBe(matches);
        await fixture.Creation.DidNotReceiveWithAnyArgs().AddStagedAsync(default!, default);
    }

    [Fact]
    public async Task MatchOrStage_QueryMutatesCallerNames_StillStagesOwnedOriginalFacts()
    {
        var names = new List<string> { "Alpha (USA)", "Alpha (Europe)" };
        var fixture = new Fixture();
        fixture.OnLookup = () => { names[0] = "Changed"; names.Clear(); };

        var staged = await fixture.Matcher.MatchOrStageBatchAsync(7, names);

        fixture.Pending.ShouldHaveSingleItem().Name.ShouldBe("Alpha");
        fixture.Receipt.Flushed = true;
        fixture.Receipt.Ids = [201];
        staged.ReadAfterFlush().Select(match => match.TitleId).ShouldBe([201, 201]);
        staged.ReadAfterFlush().Select(match => match.TitleWasCreated).ShouldBe([true, false]);
    }

    [Fact]
    public async Task MatchOrStage_ExistingOnly_CopiesLookupFactsAndRequiresNoFlush()
    {
        var fixture = new Fixture();

        var staged = await fixture.Matcher.MatchOrStageBatchAsync(7, ["Existing", "Existing"]);
        fixture.Existing["existing"] = 999;

        staged.RequiresFlush.ShouldBeFalse();
        staged.ReadAfterFlush().Select(match => match.TitleId).ShouldBe([101, 101]);
        staged.ReadAfterFlush().ShouldAllBe(match => !match.TitleWasCreated);
        fixture.Clock.Reads.ShouldBe(0);
        await fixture.Creation.DidNotReceiveWithAnyArgs().AddBatchStagedAsync(default!, default);
    }

    [Fact]
    public async Task MatchOrStage_EmptyBatch_IsImmediatelyReadableWithoutQueryOrFlush()
    {
        var fixture = new Fixture();
        fixture.Creation.ClearReceivedCalls();

        var staged = await fixture.Matcher.MatchOrStageBatchAsync(7, []);

        staged.RequiresFlush.ShouldBeFalse();
        staged.ReadAfterFlush().ShouldBeEmpty();
        fixture.Creation.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task MatchOrStage_OversizeBatch_RejectsBeforeReadingOrStaging()
    {
        var fixture = new Fixture();
        fixture.Creation.ClearReceivedCalls();

        (await Should.ThrowAsync<ArgumentOutOfRangeException>(() => fixture.Matcher.MatchOrStageBatchAsync(7, new string[501])))
            .ParamName.ShouldBe("gameNames");

        fixture.Creation.ReceivedCalls().ShouldBeEmpty();
    }

    private sealed class Fixture
    {
        public ITitleCreationStore Creation { get; } = Substitute.For<ITitleCreationStore>();
        public Dictionary<string, int> Existing { get; } = new(StringComparer.Ordinal) { ["existing"] = 101 };
        public IReadOnlyList<Title> Pending { get; private set; } = [];
        public Action? OnLookup { get; set; }
        public Clock Clock { get; } = new();
        public Receipt Receipt { get; } = new();
        public TitleMatcher Matcher { get; }
        public Fixture()
        {
            Creation.FindIdsByNormalizedNamesAsync(Arg.Any<int>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
                .Returns(_ => { OnLookup?.Invoke(); return Task.FromResult<IReadOnlyDictionary<string, int>>(Existing); });
            Creation.AddBatchStagedAsync(Arg.Any<IReadOnlyList<Title>>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Pending = call.Arg<IReadOnlyList<Title>>().ToArray();
                return Task.FromResult<IStagedTitleCreationBatch>(Receipt);
            });
            Matcher = new TitleMatcher(Creation, Clock);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public int Reads { get; private set; }
        public override DateTimeOffset GetUtcNow() => Now.AddTicks(Reads++);
    }

    private sealed class Receipt : IStagedTitleCreationBatch
    {
        public bool Flushed { get; set; }
        public int[] Ids { get; set; } = [201, 202];
        public bool RequiresFlush => !Flushed;
        public IReadOnlyList<int> ReadIdsAfterFlush() => Flushed ? Ids : throw new InvalidOperationException("Owner has not flushed.");
    }
}
