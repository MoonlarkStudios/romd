using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Romd.Admin.Application.Search;
using Romd.Application.Common.Pagination;
using Romd.Consumer.Application.Browse;
using Romd.Consumer.Application.Browse.ReadModels;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Catalog;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence.Search;

/// <summary>
///     Golden search corpus for #173 (search parity across the PostgreSQL cutover). Every case in
///     <c>search-golden-corpus.json</c> runs against the admin game search, admin title search, or
///     consumer catalog search over <see cref="SearchGoldenFixture"/> and must reproduce the
///     accepted results. Relevance-sorted cases assert membership, absence of duplicates, and
///     run-to-run determinism instead of provider-specific rank order. A case may record a
///     <c>knownDefects</c> entry for a provider: the accepted expectation stays correct, and while
///     that provider is current the test asserts the case still fails, so the entry must be removed
///     once the provider is fixed. Set <c>ROMD_SEARCH_GOLDEN_CAPTURE=1</c> to rewrite the accepted
///     results from the current provider (known-defect cases are never overwritten); the resulting
///     diff is what a human accepts.
/// </summary>
public sealed class SearchGoldenCorpusTests(SearchGoldenFixture fixture) : IClassFixture<SearchGoldenFixture>
{
    private const string CaptureVariable = "ROMD_SEARCH_GOLDEN_CAPTURE";
    private const string CurrentProvider = "postgresql";
    private const int MaxPages = 100;

    private static readonly string[] CommonFamilies =
    [
        "empty",
        "prefix",
        "case",
        "multi-term",
        "punctuation",
        "diacritics",
        "unicode",
        "quotes-operators",
        "malformed",
        "duplicate-ranks",
        "pagination",
        "description",
        "filter-platform"
    ];

    private static readonly IReadOnlyDictionary<string, string[]> SurfaceFamilies =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["AdminTitles"] =
            [
                "relevance",
                "sort-rating",
                "filter-genre",
                "filter-tracked",
                "filter-enrichment",
                "filter-completeness"
            ],
            ["AdminGames"] =
                ["relevance", "sort-year", "filter-year", "filter-manufacturer", "filter-region", "filter-bios"],
            ["ConsumerCatalog"] = ["sort-rating", "filter-genre", "filter-completeness", "ownership"]
        };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static TheoryData<string> CaseIds => [.. LoadCorpus().Cases.Select(testCase => testCase.Id)];

    [Theory]
    [MemberData(nameof(CaseIds))]
    public async Task GoldenCase_CurrentProvider_MatchesAcceptedResults(string caseId)
    {
        var corpus = LoadCorpus();
        var testCase = corpus.Cases.Single(candidate => candidate.Id == caseId);

        var observed = await ObserveAsync(testCase);
        var repeated = await ObserveAsync(testCase);

        repeated.Keys.ShouldBe(observed.Keys, "search results must be deterministic run to run");
        repeated.Error.ShouldBe(observed.Error);
        string? knownDefect = testCase.KnownDefects?.GetValueOrDefault(CurrentProvider);
        if (Environment.GetEnvironmentVariable(CaptureVariable) == "1" && knownDefect is null)
        {
            testCase.Expected = observed.Keys.ToList();
            testCase.Error = observed.Error;
            SaveCorpus(corpus);
        }

        if (knownDefect is not null)
        {
            Satisfies(observed, testCase).ShouldBeFalse(
                $"{caseId}: {CurrentProvider} no longer reproduces the recorded defect; " +
                $"remove knownDefects.{CurrentProvider} ({knownDefect})");
            return;
        }

        observed.Error.ShouldBe(testCase.Error, $"{caseId}: accepted outcome");
        switch (testCase.OrderMode)
        {
            case "Exact":
                observed.Keys.ShouldBe(testCase.Expected, $"{caseId}: accepted order");
                break;
            case "Set":
                observed.Keys.Distinct(StringComparer.Ordinal).Count().ShouldBe(
                    observed.Keys.Count,
                    $"{caseId}: relevance results must not repeat an item");
                observed.Keys.Order(StringComparer.Ordinal).ShouldBe(
                    testCase.Expected.Order(StringComparer.Ordinal),
                    $"{caseId}: accepted membership");
                break;
            default:
                throw new InvalidOperationException($"Unknown orderMode '{testCase.OrderMode}' in {caseId}.");
        }
    }

    private static bool Satisfies(Observation observed, GoldenCase testCase) =>
        observed.Error == testCase.Error
        && testCase.OrderMode switch
        {
            "Exact" => observed.Keys.SequenceEqual(testCase.Expected, StringComparer.Ordinal),
            "Set" => observed.Keys.Distinct(StringComparer.Ordinal).Count() == observed.Keys.Count
                     && observed.Keys.Order(StringComparer.Ordinal).SequenceEqual(
                         testCase.Expected.Order(StringComparer.Ordinal),
                         StringComparer.Ordinal),
            _ => throw new InvalidOperationException($"Unknown orderMode '{testCase.OrderMode}' in {testCase.Id}.")
        };

    [Fact]
    public void GoldenCorpus_CaseIds_AreUnique()
    {
        var corpus = LoadCorpus();

        corpus.Cases.Select(testCase => testCase.Id).ShouldBeUnique();
        corpus.Cases.ShouldAllBe(testCase => SurfaceFamilies.ContainsKey(testCase.Surface));
    }

    [Fact]
    public void GoldenCorpus_EverySurface_CoversRequiredBehaviourFamilies()
    {
        var corpus = LoadCorpus();

        var missing = SurfaceFamilies
            .SelectMany(surface => CommonFamilies
                .Concat(surface.Value)
                .Where(family => !corpus.Cases.Any(testCase =>
                    testCase.Surface == surface.Key && testCase.Tags.Contains(family, StringComparer.Ordinal)))
                .Select(family => $"{surface.Key}: {family}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        missing.ShouldBeEmpty("every #173 behaviour family needs at least one accepted case per surface");
    }

    private async Task<Observation> ObserveAsync(GoldenCase testCase)
    {
        var keys = new List<string>();
        string? cursor = null;
        int pages = 0;
        int limit = testCase.PageSize ?? testCase.Limit;

        try
        {
            do
            {
                var (pageKeys, nextCursor) = await RunPageAsync(testCase, cursor, limit);
                keys.AddRange(pageKeys);
                cursor = nextCursor;
                pages++;
            }
            while (cursor is not null && testCase.PageSize is not null && pages < MaxPages);
        }
        catch (Exception exception) when (exception is not ShouldAssertException)
        {
            return new Observation(keys, exception.GetType().Name);
        }

        pages.ShouldBeLessThan(MaxPages, $"{testCase.Id}: pagination never terminated");
        return new Observation(keys, null);
    }

    private async Task<(IReadOnlyList<string> Keys, string? NextCursor)> RunPageAsync(
        GoldenCase testCase,
        string? cursor,
        int limit)
    {
        await using var db = fixture.CreateContext();
        var filters = testCase.Filters ?? new GoldenFilters();

        switch (testCase.Surface)
        {
            case "AdminGames":
            {
                var page = await new SearchRepository(db).SearchGamesAsync(
                    testCase.Query,
                    new GameSearchFilters(
                        filters.PlatformId,
                        filters.Year,
                        filters.Manufacturer,
                        filters.RegionId,
                        ParseEnum(filters.Bios, BiosFilter.Exclude)),
                    Enum.Parse<GameSortField>(testCase.Sort),
                    cursor,
                    limit,
                    libraryId: null);
                return (page.Items.Select(game => $"{game.Id}:{game.Name}").ToList(), page.NextCursor);
            }
            case "AdminTitles":
            {
                var page = await new SearchRepository(db).SearchTitlesAsync(
                    testCase.Query,
                    new TitleSearchFilters(
                        filters.PlatformId,
                        filters.Genre,
                        ParseEnum(filters.ReleaseCompleteness, ReleaseCompletenessFilter.All),
                        filters.EnrichmentStatus,
                        ParseEnum(filters.Tracked, TrackedFilter.All)),
                    Enum.Parse<TitleSortField>(testCase.Sort),
                    cursor,
                    limit,
                    libraryId: null);
                return (page.Items.Select(title => $"{title.Id}:{title.Name}").ToList(), page.NextCursor);
            }
            case "ConsumerCatalog":
            {
                var result = await new ConsumerBrowseRepository(db, new ConsumerReleaseSelector()).SearchCatalogAsync(
                    new ConsumerLibraryScope(SearchGoldenFixture.UserId),
                    new ConsumerCatalogFilters(
                        testCase.Query,
                        filters.PlatformId,
                        filters.Genre,
                        ParseEnum(filters.Completeness, ConsumerCompletenessFilter.All),
                        Enum.Parse<ConsumerTitleSortField>(testCase.Sort)),
                    ConsumerReleasePreference.Default,
                    cursor,
                    limit);
                var found = result
                    .ShouldBeOfType<ConsumerLibraryReadResult<PagedList<ConsumerTitleCardData>>.Found>();
                return (
                    found.Value.Items.Select(title => $"{title.Id}:{title.Name}").ToList(),
                    found.Value.NextCursor);
            }
            default:
                throw new InvalidOperationException($"Unknown surface '{testCase.Surface}' in {testCase.Id}.");
        }
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback) where TEnum : struct, Enum =>
        value is null ? fallback : Enum.Parse<TEnum>(value);

    private static GoldenCorpus LoadCorpus() =>
        JsonSerializer.Deserialize<GoldenCorpus>(File.ReadAllText(CorpusPath()), JsonOptions)
        ?? throw new InvalidOperationException("Golden corpus is empty.");

    private static void SaveCorpus(GoldenCorpus corpus) =>
        File.WriteAllText(CorpusPath(), JsonSerializer.Serialize(corpus, JsonOptions) + Environment.NewLine);

    private static string CorpusPath() =>
        Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "Romd.Infrastructure.Tests",
            "Persistence",
            "Search",
            "search-golden-corpus.json");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Romd.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }

    private sealed record Observation(IReadOnlyList<string> Keys, string? Error);

    private sealed class GoldenCorpus
    {
        public int SchemaVersion { get; set; }
        public string CapturedFrom { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
        public List<GoldenCase> Cases { get; set; } = [];
    }

    private sealed class GoldenCase
    {
        public string Id { get; set; } = string.Empty;
        public string Surface { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = [];
        public string? Query { get; set; }
        public string Sort { get; set; } = string.Empty;
        public GoldenFilters? Filters { get; set; }
        public int Limit { get; set; } = 50;
        public int? PageSize { get; set; }
        public string OrderMode { get; set; } = "Exact";
        public Dictionary<string, string>? KnownDefects { get; set; }
        public List<string> Expected { get; set; } = [];
        public string? Error { get; set; }
    }

    private sealed class GoldenFilters
    {
        public int? PlatformId { get; set; }
        public string? Genre { get; set; }
        public string? Year { get; set; }
        public string? Manufacturer { get; set; }
        public int? RegionId { get; set; }
        public string? Bios { get; set; }
        public string? EnrichmentStatus { get; set; }
        public string? Tracked { get; set; }
        public string? ReleaseCompleteness { get; set; }
        public string? Completeness { get; set; }
    }
}
