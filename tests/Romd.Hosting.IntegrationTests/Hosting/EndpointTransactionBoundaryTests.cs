using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;
using static Romd.Hosting.IntegrationTests.Infrastructure.RepositoryPaths;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class EndpointTransactionBoundaryTests
{
    private const string CancellationFile = "src/Romd.Hosting/Endpoints/JobEndpoints.cs";
    private static readonly Regex TransactionCapability = new(
        @"\b(?<capability>IUnitOfWork|ITransaction)\b|\b(?<capability>BeginTransactionAsync|ExecuteInTransactionAsync|CommitAsync)\s*(?:<[^;\r\n]+>)?\s*\(",
        RegexOptions.CultureInvariant);
    private static readonly Regex CancellationDeclaration = new(
        @"\bprivate\s+static\s+async\s+Task\s*<\s*IResult\s*>\s+Cancel\s*\(",
        RegexOptions.CultureInvariant);

    // Frozen historical cancellation debt. Retire measured sites only after their ownership
    // moves to an application command; keep the original fixture coverage for exact counts.
    private static readonly IReadOnlyDictionary<Site, int> ExistingExceptions = new Dictionary<Site, int>
    {
        [new(CancellationFile, "Cancel", "IUnitOfWork")] = 1,
        [new(CancellationFile, "Cancel", "BeginTransactionAsync")] = 2,
        [new(CancellationFile, "Cancel", "CommitAsync")] = 2
    };

    // Cancellation is now command-owned. No endpoint transaction capabilities remain allowed.
    private static readonly ImmutableHashSet<Site> RetiredExceptions =
    [
        new(CancellationFile, "Cancel", "IUnitOfWork"),
        new(CancellationFile, "Cancel", "BeginTransactionAsync"),
        new(CancellationFile, "Cancel", "CommitAsync")
    ];

    private static readonly IReadOnlyDictionary<Site, int> CurrentExceptions = ExistingExceptions
        .Where(entry => !RetiredExceptions.Contains(entry.Key)).ToDictionary();

    private const string CancellationFixture = """
        private static async Task<IResult> Cancel(IUnitOfWork work)
        {
            await work.BeginTransactionAsync(ct);
            await transaction.CommitAsync(ct);
            if (artwork)
            {
                await work.BeginTransactionAsync(ct);
                await transaction.CommitAsync(ct);
            }
        }
        """;

    [Fact]
    public void Endpoints_ApplicationTransactions_BelongToCommands()
    {
        var transactionSites = Directory.GetFiles(FindRepositoryDirectory("src/Romd.Hosting/Endpoints"), "*.cs", SearchOption.AllDirectories)
            .Where(IsRepositorySourceFile)
            .SelectMany(path => Scan(ToRepositoryRelativePath(path), File.ReadAllText(path)));

        FindUnreviewedOrStaleSites(transactionSites, CurrentExceptions).ShouldBeEmpty();
    }

    [Fact]
    public void CancellationDebt_RetirementsPreserveTheFrozenMeasuredShape()
    {
        ExistingExceptions.Count.ShouldBe(3);
        ExistingExceptions.Values.Sum().ShouldBe(5);
        RetiredExceptions.Count.ShouldBe(3);
        RetiredExceptions.SetEquals(ExistingExceptions.Keys).ShouldBeTrue();
        CurrentExceptions.ShouldBeEmpty();
    }

    [Fact]
    public void Guard_ReintroducedCancellationCapabilities_RefusesEveryRetiredSite()
    {
        var failures = FindUnreviewedOrStaleSites(Scan(CancellationFile, CancellationFixture), CurrentExceptions);

        failures.Length.ShouldBe(3);
        failures.ShouldContain(failure => failure.Contains("Cancel/IUnitOfWork: expected 0, found 1", StringComparison.Ordinal));
        failures.ShouldContain(failure => failure.Contains("Cancel/BeginTransactionAsync: expected 0, found 2", StringComparison.Ordinal));
        failures.ShouldContain(failure => failure.Contains("Cancel/CommitAsync: expected 0, found 2", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("IUnitOfWork work", "IUnitOfWork")]
    [InlineData("ITransaction transaction", "ITransaction")]
    [InlineData("await work.BeginTransactionAsync(ct)", "BeginTransactionAsync")]
    [InlineData("await work.ExecuteInTransactionAsync(operation, logger, ct)", "ExecuteInTransactionAsync")]
    [InlineData("await work.ExecuteInTransactionAsync<Guid>(operation, logger, ct)", "ExecuteInTransactionAsync")]
    [InlineData("await transaction.CommitAsync(ct)", "CommitAsync")]
    public void Guard_TransactionCapabilities_DetectsRegressions(string source, string capability) =>
        Scan("New.cs", source).ShouldHaveSingleItem().Capability.ShouldBe(capability);

    [Fact]
    public void Guard_ExactRecordedCancellationSites_AreAccepted() =>
        FindUnreviewedOrStaleSites(Scan(CancellationFile, CancellationFixture)).ShouldBeEmpty();

    [Fact]
    public void Guard_TransactionInNewFile_RefusesUnreviewedSite()
    {
        var sites = Scan(CancellationFile, CancellationFixture).Concat(Scan("New.cs", "await work.BeginTransactionAsync(ct);"));

        FindUnreviewedOrStaleSites(sites).ShouldHaveSingleItem()
            .ShouldContain("New.cs/<outside Cancel>/BeginTransactionAsync: expected 0, found 1");
    }

    [Fact]
    public void Guard_NewTransactionInAllowedFile_RefusesDifferentMethod()
    {
        string source = CancellationFixture + "private static async Task<IResult> Start() { await work.BeginTransactionAsync(ct); }";

        FindUnreviewedOrStaleSites(Scan(CancellationFile, source)).ShouldHaveSingleItem()
            .ShouldContain("<outside Cancel>/BeginTransactionAsync: expected 0, found 1");
    }

    [Fact]
    public void Guard_TransactionMovedToDifferentMethod_RefusesEvenWhenFileCountIsUnchanged()
    {
        const string begin = "await work.BeginTransactionAsync(ct);";
        string source = CancellationFixture.Remove(CancellationFixture.IndexOf(begin, StringComparison.Ordinal), begin.Length)
            + "private static async Task<IResult> Start() { await work.BeginTransactionAsync(ct); }";

        var failures = FindUnreviewedOrStaleSites(Scan(CancellationFile, source));

        failures.Length.ShouldBe(2);
        failures.ShouldContain(failure => failure.Contains("Cancel/BeginTransactionAsync: expected 2, found 1", StringComparison.Ordinal));
        failures.ShouldContain(failure => failure.Contains("<outside Cancel>/BeginTransactionAsync: expected 0, found 1", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("IUnitOfWork duplicate;", "IUnitOfWork", 2)]
    [InlineData("await work.BeginTransactionAsync(ct);", "BeginTransactionAsync", 3)]
    [InlineData("await transaction.CommitAsync(ct);", "CommitAsync", 3)]
    [InlineData("ITransaction transaction;", "ITransaction", 1)]
    [InlineData("await work.ExecuteInTransactionAsync<Guid>(operation, logger, ct);", "ExecuteInTransactionAsync", 1)]
    public void Guard_AddedCapabilityInsideCancellation_RefusesGrowth(string duplicate, string capability, int count)
    {
        string source = CancellationFixture.Replace("if (artwork)", duplicate + "if (artwork)", StringComparison.Ordinal);

        FindUnreviewedOrStaleSites(Scan(CancellationFile, source)).ShouldHaveSingleItem()
            .ShouldContain($"Cancel/{capability}: expected {count - 1}, found {count}");
    }

    [Fact]
    public void Guard_RemovedCapability_RefusesStaleException()
    {
        string source = CancellationFixture.Replace("await transaction.CommitAsync(ct);", "", StringComparison.Ordinal);

        FindUnreviewedOrStaleSites(Scan(CancellationFile, source)).ShouldHaveSingleItem()
            .ShouldContain("Cancel/CommitAsync: expected 2, found 0");
    }

    [Fact]
    public void Guard_OverloadedCancellation_DoesNotBroadenMethodException()
    {
        string source = CancellationFixture + "private static async Task<IResult> Cancel() { await transaction.CommitAsync(ct); }";

        FindUnreviewedOrStaleSites(Scan(CancellationFile, source)).ShouldNotBeEmpty();
    }

    [Fact]
    public void Guard_CommentsAndLiterals_DoNotChangeMethodBoundsOrCapabilities()
    {
        string source = CancellationFixture.Replace("if (artwork)", """
            // } CommitAsync(ct); IUnitOfWork
            var message = "} BeginTransactionAsync(ct);";
            /* } ITransaction { */
            if (artwork)
            """, StringComparison.Ordinal);

        FindUnreviewedOrStaleSites(Scan(CancellationFile, source)).ShouldBeEmpty();
    }

    private static IEnumerable<Site> Scan(string file, string source)
    {
        string code = EnvelopeSourceScan.Blank(source, keepStrings: false);
        var cancellation = CancellationDeclaration.Matches(code);
        int start = -1, end = -1;
        if (file == CancellationFile && cancellation.Count == 1)
        {
            start = cancellation[0].Index;
            int parameters = code.IndexOf('(', start);
            int afterParameters = AfterBalanced(code, parameters, '(', ')');
            int body = afterParameters;
            while (body >= 0 && body < code.Length && char.IsWhiteSpace(code[body]))
                body++;
            if (body >= 0 && body < code.Length && code[body] == '{')
                end = AfterBalanced(code, body, '{', '}');
        }
        return TransactionCapability.Matches(code).Select(match => new Site(file,
            match.Index >= start && match.Index < end ? "Cancel" : "<outside Cancel>", match.Groups["capability"].Value));
    }

    private static int AfterBalanced(string code, int start, char open, char close)
    {
        int depth = 0;
        for (int index = start; index < code.Length; index++)
        {
            if (code[index] == open)
                depth++;
            else if (code[index] == close && --depth == 0)
                return index + 1;
        }
        return -1;
    }

    private static string[] FindUnreviewedOrStaleSites(IEnumerable<Site> actual, IReadOnlyDictionary<Site, int>? reviewed = null)
    {
        reviewed ??= ExistingExceptions;
        var counts = actual.GroupBy(site => site).ToDictionary(group => group.Key, group => group.Count());
        return counts.Keys.Union(reviewed.Keys)
            .Where(site => counts.GetValueOrDefault(site) != reviewed.GetValueOrDefault(site))
            .Select(site => $"{site.File}/{site.Method}/{site.Capability}: expected {reviewed.GetValueOrDefault(site)}, found {counts.GetValueOrDefault(site)}")
            .Order(StringComparer.Ordinal).ToArray();
    }

    private sealed record Site(string File, string Method, string Capability);
}
