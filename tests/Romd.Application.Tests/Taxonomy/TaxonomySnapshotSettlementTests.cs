using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Taxonomy;
using Romd.Admin.Application.Taxonomy.Queries.GetLanguage;
using Romd.Admin.Application.Taxonomy.Queries.GetRegion;
using Romd.Admin.Application.Taxonomy.Queries.ListLanguageAliases;
using Romd.Admin.Application.Taxonomy.Queries.ListLanguages;
using Romd.Admin.Application.Taxonomy.Queries.ListRegionAliases;
using Romd.Admin.Application.Taxonomy.Queries.ListRegions;
using Romd.Admin.Application.Taxonomy.ReadModels;
using Romd.Application.Common.Pagination;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Taxonomy;

public sealed class TaxonomySnapshotSettlementTests
{
    public static IEnumerable<object[]> Failures()
    {
        foreach (bool language in new[] { false, true })
            foreach (string kind in new[] { "get", "list", "aliases" })
                foreach (bool complete in new[] { false, true })
                    foreach (bool cancel in new[] { false, true })
                        yield return [language, kind, complete, cancel];
    }

    public static IEnumerable<object[]> AcknowledgedReads()
    {
        foreach (bool language in new[] { false, true })
            foreach (string kind in new[] { "get", "list", "aliases" })
                foreach (bool cancel in new[] { false, true })
                    yield return [language, kind, cancel];
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Handle_ReadOrCompletionAndCleanupFail_PreservesPrimaryWithoutRetry(bool language, string kind, bool complete, bool cancel)
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        Exception cause = cancel ? new OperationCanceledException("primary snapshot cancellation", cancellation.Token)
            : new IOException("primary snapshot read or completion failure");
        if (complete)
            fixture.CompletionFault = cause;
        else
        {
            fixture.ReadFault = cause;
            fixture.FailAtRead = kind == "aliases" ? 2 : 1;
        }

        Exception? observed = null;
        try
        { await fixture.HandleAsync(language, kind, cancellation.Token); }
        catch (Exception error) { observed = error; }

        observed.ShouldBeSameAs(cause);
        if (cancel)
            observed.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        fixture.Reads.ShouldBe(kind == "aliases" ? 2 : 1);
        fixture.Completions.ShouldBe(complete ? 1 : 0);
        fixture.Completed.ShouldBeFalse();
        fixture.Disposals.ShouldBe(1);
        fixture.Tokens.ShouldAllBe(token => token == cancellation.Token);
        await fixture.Snapshots.Received(1).BeginAsync(cancellation.Token);
    }

    [Theory]
    [MemberData(nameof(AcknowledgedReads))]
    public async Task Handle_CompletionAcknowledgedAndCleanupFails_ReturnsCapturedSnapshot(bool language, string kind, bool cancel)
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        if (cancel)
            fixture.AfterCompletion = cancellation.Cancel;

        var result = await fixture.HandleAsync(language, kind, cancellation.Token);

        result.Value.ShouldBeSameAs(fixture.Expected(kind));
        fixture.Completed.ShouldBeTrue();
        fixture.Reads.ShouldBe(kind == "aliases" ? 2 : 1);
        fixture.Completions.ShouldBe(1);
        fixture.Disposals.ShouldBe(1);
        fixture.Tokens.ShouldAllBe(token => token == cancellation.Token);
        await fixture.Snapshots.Received(1).BeginAsync(cancellation.Token);
    }

    [Theory]
    [InlineData(false, "get")]
    [InlineData(true, "get")]
    [InlineData(false, "aliases")]
    [InlineData(true, "aliases")]
    public async Task Handle_MissingRow_CompletesSnapshotAndPreservesNotFoundDespiteCleanup(bool language, string kind)
    {
        var fixture = new Fixture { Missing = true };

        var result = await fixture.HandleAsync(language, kind);

        result.FirstError.Code.ShouldBe("ReferenceResource.NotFound");
        fixture.Reads.ShouldBe(1);
        fixture.Completions.ShouldBe(1);
        fixture.Completed.ShouldBeTrue();
        fixture.Disposals.ShouldBe(1);
    }

    [Theory]
    [InlineData(false, "list")]
    [InlineData(true, "list")]
    [InlineData(false, "aliases")]
    [InlineData(true, "aliases")]
    public async Task Handle_InvalidCursor_RejectsBeforeOpeningSnapshot(bool language, string kind)
    {
        var fixture = new Fixture();

        var result = await fixture.HandleAsync(language, kind, cursor: "not-a-cursor");

        result.FirstError.Code.ShouldBe("Taxonomy.InvalidCursor");
        fixture.Reads.ShouldBe(0);
        fixture.Completions.ShouldBe(0);
        fixture.Disposals.ShouldBe(0);
        await fixture.Snapshots.DidNotReceiveWithAnyArgs().BeginAsync();
    }

    private sealed class Fixture
    {
        private const string Key = "dat-atlantis";
        private readonly IRegionRepository regions = Substitute.For<IRegionRepository>();
        private readonly ILanguageRepository languages = Substitute.For<ILanguageRepository>();
        private readonly IReadSnapshotTransaction snapshot = Substitute.For<IReadSnapshotTransaction>();
        private readonly TaxonomyEntry entry = new(Key, null, null, "Atlantis", null, 9, false, true, null,
            new TaxonomyAliasPage([new(7, "Atlantis alias", "Installation")], false), "revision");
        private readonly PagedList<TaxonomyEntry> entries;
        private readonly PagedList<TaxonomyAlias> aliases = new([new(7, "Atlantis alias", "Installation")], null, false);
        private bool inside;
        public IReadSnapshotTransactionFactory Snapshots { get; } = Substitute.For<IReadSnapshotTransactionFactory>();
        public bool Missing { get; init; }
        public Exception? ReadFault { get; set; }
        public int FailAtRead { get; set; }
        public Exception? CompletionFault { get; set; }
        public Action? AfterCompletion { get; set; }
        public int Reads { get; private set; }
        public int Completions { get; private set; }
        public int Disposals { get; private set; }
        public bool Completed { get; private set; }
        public List<CancellationToken> Tokens { get; } = [];

        public Fixture()
        {
            entries = new([entry], null, false);
            Snapshots.BeginAsync(Arg.Any<CancellationToken>()).Returns(call =>
            {
                inside.ShouldBeFalse();
                Tokens.Add(call.Arg<CancellationToken>());
                inside = true;
                return snapshot;
            });
            regions.GetWithAliasesAsync(Key, Arg.Any<CancellationToken>()).Returns(call => Read<TaxonomyEntry?>(Missing ? null : entry, call.Arg<CancellationToken>()));
            languages.GetWithAliasesAsync(Key, Arg.Any<CancellationToken>()).Returns(call => Read<TaxonomyEntry?>(Missing ? null : entry, call.Arg<CancellationToken>()));
            regions.ListWithAliasesAsync(Arg.Any<TaxonomyPageRequest>(), Arg.Any<CancellationToken>()).Returns(call => Read(entries, call.Arg<CancellationToken>()));
            languages.ListWithAliasesAsync(Arg.Any<TaxonomyPageRequest>(), Arg.Any<CancellationToken>()).Returns(call => Read(entries, call.Arg<CancellationToken>()));
            regions.GetIdByKeyAsync(Key, Arg.Any<CancellationToken>()).Returns(call => Read<int?>(Missing ? null : 3, call.Arg<CancellationToken>()));
            languages.GetIdByKeyAsync(Key, Arg.Any<CancellationToken>()).Returns(call => Read<int?>(Missing ? null : 3, call.Arg<CancellationToken>()));
            regions.ListAliasesAsync(3, Arg.Any<TaxonomyAliasPageRequest>(), Arg.Any<CancellationToken>()).Returns(call => Read(aliases, call.Arg<CancellationToken>()));
            languages.ListAliasesAsync(3, Arg.Any<TaxonomyAliasPageRequest>(), Arg.Any<CancellationToken>()).Returns(call => Read(aliases, call.Arg<CancellationToken>()));
            snapshot.CompleteAsync(Arg.Any<CancellationToken>()).Returns(call =>
            {
                inside.ShouldBeTrue();
                Tokens.Add(call.Arg<CancellationToken>());
                Completions++;
                if (CompletionFault is not null)
                    throw CompletionFault;
                Completed = true;
                AfterCompletion?.Invoke();
                return Task.CompletedTask;
            });
            snapshot.DisposeAsync().Returns(_ =>
            {
                inside.ShouldBeTrue();
                inside = false;
                Disposals++;
                return ValueTask.FromException(new IOException("secondary snapshot cleanup failure"));
            });
        }

        private T Read<T>(T value, CancellationToken ct)
        {
            inside.ShouldBeTrue();
            Completed.ShouldBeFalse();
            Tokens.Add(ct);
            Reads++;
            if (ReadFault is not null && Reads == FailAtRead)
                throw ReadFault;
            return value;
        }

        public object Expected(string kind) => kind switch { "get" => entry, "list" => entries, _ => aliases };

        public async Task<ErrorOr<object>> HandleAsync(bool language, string kind, CancellationToken ct = default, string? cursor = null)
        {
            if (language)
                return kind switch
                {
                    "get" => Box(await new GetLanguageQueryHandler(languages, Snapshots).HandleAsync(new(Key), ct)),
                    "list" => Box(await new ListLanguagesQueryHandler(languages, Snapshots).HandleAsync(new(cursor), ct)),
                    _ => Box(await new ListLanguageAliasesQueryHandler(languages, Snapshots).HandleAsync(new(Key, cursor), ct))
                };
            return kind switch
            {
                "get" => Box(await new GetRegionQueryHandler(regions, Snapshots).HandleAsync(new(Key), ct)),
                "list" => Box(await new ListRegionsQueryHandler(regions, Snapshots).HandleAsync(new(cursor), ct)),
                _ => Box(await new ListRegionAliasesQueryHandler(regions, Snapshots).HandleAsync(new(Key, cursor), ct))
            };
        }

        private static ErrorOr<object> Box<T>(ErrorOr<T> result) => result.IsError ? result.Errors : ErrorOrFactory.From<object>(result.Value!);
    }
}
