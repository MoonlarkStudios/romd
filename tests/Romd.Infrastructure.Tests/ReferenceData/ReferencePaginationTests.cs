using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Platform;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Security;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;
using Romd.Domain.Source.Platform;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.ReferenceData;
using Romd.Persistence.Systems;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.ReferenceData;

public sealed class ReferencePaginationTests
{
    [Fact]
    public async Task InstallationGrowth_BeyondOneHundred_TraversesEveryKey_AndSetupSelectsBeforeAggregating()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        for (var i = 0; i < 113; i++)
        {
            var key = $"local-{i:000}";
            db.Companies.Add(new CompanyEntity { Key = key, Name = key, BaseName = key, Ownership = ReferenceOwnership.Installation });
            db.Platforms.Add(new PlatformEntity
            {
                CanonicalKey = key,
                Ownership = ReferenceOwnership.Installation,
                Name = key,
                BaseName = key,
                BaseCompactLabel = key,
                ShortName = key
            });
        }
        // Historical relational rows are not reference resources and must never trigger an unregistered-key 500.
        db.Platforms.Add(new PlatformEntity { Name = "Historical", ShortName = "historical" });
        await db.SaveChangesAsync();
        var systems = new SystemReferenceReader(db);
        var companies = new CompanyReferenceReader(db);
        var first = await systems.ListAsync(ReferencePageRequest.Create("systems").Value, default);
        first.Items.Count.ShouldBe(100);
        first.HasNextPage.ShouldBeTrue();
        first.NextCursor.ShouldNotBeNull();
        var last = await systems.ListAsync(ReferencePageRequest.Create("systems", cursor: first.NextCursor).Value, default);
        last.Items.Count.ShouldBe(13);
        last.HasNextPage.ShouldBeFalse();
        last.NextCursor.ShouldBeNull();
        first.Items.Concat(last.Items).Select(x => x.Key).ShouldBe(Enumerable.Range(0, 113).Select(i => $"local-{i:000}"));
        first.Items.ShouldAllBe(x => x.Revision.Length == 64);
        var companyFirst = await companies.ListAsync(ReferencePageRequest.Create("companies").Value, default);
        var companyLast = await companies.ListAsync(ReferencePageRequest.Create("companies", cursor: companyFirst.NextCursor, limit: 17).Value, default);
        companyFirst.Items.Count.ShouldBe(100);
        companyLast.Items.Count.ShouldBe(13);
        companyFirst.Items.Concat(companyLast.Items).Select(x => x.Key).ShouldBe(first.Items.Concat(last.Items).Select(x => x.Key));
        companyFirst.Items.ShouldAllBe(x => x.Revision.Length == 64);

        var capture = new Capture();
        await using var observed = new RomdDbContext(new DbContextOptionsBuilder<RomdDbContext>().UseNpgsql(database.ConnectionString).AddInterceptors(capture).Options);
        var setup = Service(observed);
        var page = await setup.ListAsync(ReferencePageRequest.Create("setup", limit: 2).Value, default);
        page.Items.Select(x => x.SystemId).ShouldBe(["local-000", "local-001"]);
        capture.Commands.Count.ShouldBe(3);
        capture.Commands[0].ShouldContain("LIMIT");
        capture.Commands[0].ShouldNotContain("DatFiles");
        capture.Commands[1].ShouldContain("DatFiles");
        capture.Commands[1].ShouldContain("ANY");
        capture.Commands.ShouldAllBe(x => !x.Contains("PlatformAliases", StringComparison.Ordinal));
        observed.Database.CurrentTransaction.ShouldBeNull();
    }

    [Fact]
    public async Task ImportedUnicodeAndPunctuationKeys_ContinueAcrossEveryBrowseBoundary()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var document = JsonNode.Parse(BundledReferenceData.Document.GetRawText())!;
        string[] keys = ["Élan v2!", "Ω / Core", "𐐀 + v2"];
        for (var i = 0; i < keys.Length; i++)
        {
            document["companies"]![keys[i]] = JsonSerializer.SerializeToNode(new { name = "Boundary company " + i });
            document["systems"]![keys[i]] = JsonSerializer.SerializeToNode(new
            {
                name = "Boundary system " + i,
                compactLabel = "B" + i,
                manufacturerIds = Array.Empty<string>(),
                aliases = Array.Empty<string>(),
                providerMappings = new Dictionary<string, string>()
            });
        }
        await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.For(db), clock: TimeProvider.System, document: JsonSerializer.SerializeToElement(document));
        var systems = new SystemReferenceReader(db);
        var companies = new CompanyReferenceReader(db);
        List<string> systemKeys = [], companyKeys = [], setupKeys = [];
        string? systemCursor = null, companyCursor = null, setupCursor = null;
        do
        {
            var page = await systems.ListAsync(ReferencePageRequest.Create("systems", query: "Boundary", cursor: systemCursor, limit: 1).Value, default);
            systemKeys.Add(page.Items.ShouldHaveSingleItem().Key);
            systemCursor = page.NextCursor;
            page.HasNextPage.ShouldBe(systemCursor is not null);
        } while (systemCursor is not null);
        do
        {
            var page = await companies.ListAsync(ReferencePageRequest.Create("companies", query: "Boundary", cursor: companyCursor, limit: 1).Value, default);
            companyKeys.Add(page.Items.ShouldHaveSingleItem().Key);
            companyCursor = page.NextCursor;
            page.HasNextPage.ShouldBe(companyCursor is not null);
        } while (companyCursor is not null);
        do
        {
            var page = await Service(db).ListAsync(ReferencePageRequest.Create("setup", query: "Boundary", cursor: setupCursor, limit: 1).Value, default);
            setupKeys.Add(page.Items.ShouldHaveSingleItem().SystemId!);
            setupCursor = page.NextCursor;
            page.HasNextPage.ShouldBe(setupCursor is not null);
        } while (setupCursor is not null);
        systemKeys.ShouldBe(keys);
        companyKeys.ShouldBe(keys);
        setupKeys.ShouldBe(keys);
    }

    [Theory]
    [InlineData("Élan—Station", "elan station")]
    [InlineData("ΩΜΈΓΑ", "ωμεγα")]
    [InlineData("ПРИСТАВКА", "приставка")]
    [InlineData("𐐀-console", "𐐨console")]
    public async Task SetupSearch_UsesUnicodeInvariantAccentPunctuationIntent_AndAliasExists(string name, string query)
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var platform = new PlatformEntity
        {
            CanonicalKey = "local-search",
            Ownership = ReferenceOwnership.Installation,
            Name = "Unrelated",
            BaseName = "Unrelated",
            BaseCompactLabel = "SEARCH",
            ShortName = "SEARCH"
        };
        db.Platforms.Add(platform);
        await db.SaveChangesAsync();
        db.PlatformAliases.AddRange(Enumerable.Range(0, 120).Select(i => new PlatformAliasEntity
        {
            PlatformId = platform.Id,
            Type = PlatformAliasType.Name,
            Value = $"Other alias {i}",
            NormalizedValue = $"other alias {i}"
        }));
        db.PlatformAliases.Add(new PlatformAliasEntity
        {
            PlatformId = platform.Id,
            Type = PlatformAliasType.Name,
            Value = name,
            NormalizedValue = "distinct alias"
        });
        await db.SaveChangesAsync();
        var page = await Service(db).ListAsync(ReferencePageRequest.Create("setup", query: query, limit: 1).Value, default);
        page.Items.ShouldHaveSingleItem().SystemId.ShouldBe(platform.CanonicalKey);
        page.HasNextPage.ShouldBeFalse();
        await db.Platforms.Where(x => x.Id == platform.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.Name, name).SetProperty(p => p.BaseName, name));
        var systems = await new SystemReferenceReader(db).ListAsync(ReferencePageRequest.Create("systems", query: query).Value, default);
        systems.Items.ShouldHaveSingleItem().Name.ShouldBe(name);
    }

    [Fact]
    public async Task Setup_ConcurrentAtomicEdit_BetweenSelectionAndAssembly_ReturnsOneObservation()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var writer = database.CreateContext();
        var commands = new TypedReferenceTestCommands(writer);
        var before = (await commands.CreateSystem(new("local-consistent", "Before", "CONS"))).Value;
        var gate = new BetweenSelectionAndAssembly();
        await using var reader = new RomdDbContext(new DbContextOptionsBuilder<RomdDbContext>().UseNpgsql(database.ConnectionString).AddInterceptors(gate).Options);
        var reading = Service(reader).ListAsync(ReferencePageRequest.Create("setup", limit: 1).Value, default);
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            (await commands.UpdateSystem("local-consistent", new SystemPatchDto { Name = "After" }, before.Revision)).IsError.ShouldBeFalse();
            gate.Resume.TrySetResult();
            (await reading).Items.ShouldHaveSingleItem().Name.ShouldBe("Before");
            reader.Database.CurrentTransaction.ShouldBeNull();
        }
        finally { gate.Resume.TrySetResult(); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void InvalidLimits_AreRejectedBeforeReadersCanExecute(int limit)
    {
        ReferencePageRequest.Create("systems", limit: limit).IsError.ShouldBeTrue();
        ReferenceSnapshotPageRequest.Create("systems", new string('a', 64), limit: limit).IsError.ShouldBeTrue();
    }
    [Fact]
    public void Cursors_AreBoundToScopeQuerySortAndPublishedRevision()
    {
        var request = ReferencePageRequest.Create("Admin:systems", query: "Élan").Value;
        var cursor = request.CursorFor("local-last");
        ReferencePageRequest.Create("Admin:systems", query: "elan", cursor: cursor).IsError.ShouldBeFalse();
        ReferencePageRequest.Create("Consumer:systems", query: "elan", cursor: cursor).IsError.ShouldBeTrue();
        ReferencePageRequest.Create("Admin:companies", query: "elan", cursor: cursor).IsError.ShouldBeTrue();
        ReferencePageRequest.Create("Admin:systems", query: "different", cursor: cursor).IsError.ShouldBeTrue();
        ReferencePageRequest.Create("Admin:systems", sortBy: "name:desc").IsError.ShouldBeTrue();
        ReferencePageRequest.Create("Admin:systems", cursor: "").IsError.ShouldBeTrue();
        ReferencePageRequest.Create("Admin:systems", query: "!!!").IsError.ShouldBeTrue();
        var snapshot = ReferenceSnapshotPageRequest.Create("Admin:systems", new string('a', 64), limit: 1).Value;
        var page = snapshot.Read(new[] { "local-z", "local-a", "local-m" }, x => x).Value;
        page.Items.ShouldBe(["local-z"]);
        var next = ReferenceSnapshotPageRequest.Create("Admin:systems", new string('a', 64), page.NextCursor, 2).Value.Read(new[] { "local-z", "local-a", "local-m" }, x => x).Value;
        next.Items.ShouldBe(["local-a", "local-m"]);
        next.NextCursor.ShouldBeNull();
        ReferenceSnapshotPageRequest.Create("Admin:systems", new string('b', 64), page.NextCursor).IsError.ShouldBeTrue();
        ReferenceSnapshotPageRequest.Create("Admin:companies", new string('a', 64), page.NextCursor).IsError.ShouldBeTrue();
        snapshot.Read(new[] { "local-other" }, x => x).IsError.ShouldBeFalse();
        ReferenceSnapshotPageRequest.Create("Admin:systems", new string('a', 64), page.NextCursor).Value.Read(new[] { "local-other" }, x => x).IsError.ShouldBeTrue();
        var ratings = ReferenceSnapshotPageRequest.Create("ratings", new string('a', 64), limit: 1).Value;
        var ratingPage = ratings.Read(new[] { ("BoardZ", "E"), ("BoardA", "E") }, x => x.Item1 + ":" + x.Item2).Value;
        ReferenceSnapshotPageRequest.Create("ratings", new string('a', 64), ratingPage.NextCursor).Value
            .Read(new[] { ("BoardZ", "E"), ("BoardA", "E") }, x => x.Item1 + ":" + x.Item2).Value.Items.ShouldBe([("BoardA", "E")]);
    }
    private static SystemSetupService Service(RomdDbContext db) => new(db, Substitute.For<IDatReplacementReview>(),
        Substitute.For<IPlatformHeaderResolver>());
    private sealed class Capture : DbCommandInterceptor
    {
        internal List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Commands.Add(command.CommandText); return ValueTask.FromResult(result); }
    }
    private sealed class BetweenSelectionAndAssembly : DbCommandInterceptor
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int paused;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("DatFiles", StringComparison.Ordinal) && Interlocked.Exchange(ref paused, 1) == 0)
            { Reached.TrySetResult(); await Resume.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); }
            return result;
        }
    }
}
