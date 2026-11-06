using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Account;
using Romd.Consumer.Application.Account.Commands.UpdateConsumerUserSettings;
using Romd.Consumer.Application.Browse;
using Romd.Domain.ReferenceData;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Identity;

public sealed class ConsumerSettingsResponseSettlementTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Handle_AcknowledgedUpsert_ReturnsPreparedKeysWithoutPostWriteReads(bool replacePreference, bool cancel)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var userId = await SeedAsync(database);
        using var cancellation = new CancellationTokenSource();
        var reads = new ReadProbe();
        await using var db = new RomdDbContext(database.CreateOptions(options => options.AddInterceptors(reads)));
        var keys = new ConsumerTaxonomyKeys(db);
        var store = new ObservedStore(new ConsumerUserSettingsStore(db, TimeProvider.System, keys))
        {
            AfterAcknowledgement = () =>
            {
                reads.Acknowledged = true;
                if (cancel)
                    cancellation.Cancel();
            }
        };
        var handler = new UpdateConsumerUserSettingsCommandHandler(CurrentUser(userId), store, keys);
        var preference = replacePreference ? new Romd.Contracts.Consumer.Account.ConsumerReleasePreferenceDto(["japan"], ["en"], "oldestFirst") : null;

        var result = await handler.HandleAsync(new("light", preference), cancellation.Token);

        result.Value.Theme.ShouldBe("light");
        result.Value.ReleasePreference.PreferredRegionKeys.ShouldBe(replacePreference ? ["japan"] : ["europe"]);
        result.Value.ReleasePreference.PreferredLanguageKeys.ShouldBe(["en"]);
        result.Value.ReleasePreference.RevisionStrategy.ShouldBe(replacePreference ? "oldestFirst" : "newestFirst");
        store.Saves.ShouldBe(1);
        reads.Acknowledged.ShouldBeTrue();
        reads.PostAcknowledgementReads.ShouldBe(0);
        await AssertPersistedAsync(database, "light", replacePreference ? 4 : 3,
            replacePreference ? ConsumerRevisionPreference.OldestFirst : ConsumerRevisionPreference.NewestFirst);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_ResponseQueryFailsBeforeUpsert_LeavesPreviousSettingsUnchanged(bool cancel)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var userId = await SeedAsync(database);
        await using var db = database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        Exception cause = cancel ? new OperationCanceledException("response key read cancelled", cancellation.Token)
            : new IOException("response key read failed");
        var realKeys = new ConsumerTaxonomyKeys(db);
        var keys = new FailingResponseKeys(realKeys, cause);
        var store = new ObservedStore(new ConsumerUserSettingsStore(db, TimeProvider.System, realKeys));
        var handler = new UpdateConsumerUserSettingsCommandHandler(CurrentUser(userId), store, keys);

        var error = await CaptureAsync(() => handler.HandleAsync(new("light", null), cancellation.Token));

        error.ShouldBeSameAs(cause);
        if (cancel)
            error.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        keys.CompletedResponseQueries.ShouldBe(1);
        store.Saves.ShouldBe(0);
        await AssertPersistedAsync(database, "dark", 3, ConsumerRevisionPreference.NewestFirst);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Handle_UpsertAcknowledgementFault_DoesNotReportPreparedSuccessOrRetry(bool afterWrite, bool cancel)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var userId = await SeedAsync(database);
        await using var db = database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        Exception cause = cancel ? new OperationCanceledException("uncertain upsert acknowledgement", cancellation.Token)
            : new IOException("uncertain upsert acknowledgement");
        var keys = new ConsumerTaxonomyKeys(db);
        var store = new ObservedStore(new ConsumerUserSettingsStore(db, TimeProvider.System, keys))
        {
            Fault = cause,
            FailAfterWrite = afterWrite
        };
        var handler = new UpdateConsumerUserSettingsCommandHandler(CurrentUser(userId), store, keys);

        var error = await CaptureAsync(() => handler.HandleAsync(new("light", null), cancellation.Token));

        error.ShouldBeSameAs(cause);
        if (cancel)
            error.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        store.Saves.ShouldBe(1);
        await AssertPersistedAsync(database, afterWrite ? "light" : "dark", 3, ConsumerRevisionPreference.NewestFirst);
    }

    private static ICurrentUser CurrentUser(Guid userId)
    {
        var user = Substitute.For<ICurrentUser>();
        user.IsAuthenticated.Returns(true);
        user.UserId.Returns(userId);
        return user;
    }

    private static async Task<Guid> SeedAsync(PostgreSqlTestDatabase database)
    {
        await using var db = database.CreateContext();
        var user = new RomdUser { UserName = "response-owner" };
        db.Users.Add(user);
        db.Regions.AddRange(
            new RegionEntity { Id = 3, Name = "Europe", CanonicalKey = "europe", BaseName = "Europe", Ownership = ReferenceOwnership.Romd, BuiltInVersion = 1 },
            new RegionEntity { Id = 4, Name = "Japan", CanonicalKey = "japan", BaseName = "Japan", Ownership = ReferenceOwnership.Romd, BuiltInVersion = 1 });
        db.GameLanguages.Add(new GameLanguageEntity
        {
            Id = 7,
            Name = "English",
            Code = "en",
            CanonicalKey = "en",
            BaseName = "English",
            Ownership = ReferenceOwnership.Romd,
            BuiltInVersion = 1
        });
        await db.SaveChangesAsync();
        await new ConsumerUserSettingsStore(db, TimeProvider.System, new ConsumerTaxonomyKeys(db))
            .SaveSettingsAsync(user.Id, new("dark", new([3], [7], ConsumerRevisionPreference.NewestFirst)));
        return user.Id;
    }

    private static async Task AssertPersistedAsync(PostgreSqlTestDatabase database, string theme, int region, ConsumerRevisionPreference strategy)
    {
        await using var read = database.CreateContext();
        var row = await read.Set<ConsumerUserSettingsEntity>().AsNoTracking().SingleAsync();
        row.Theme.ShouldBe(theme);
        JsonSerializer.Deserialize<int[]>(row.PreferredRegionIdsJson).ShouldBe([region]);
        JsonSerializer.Deserialize<int[]>(row.PreferredLanguageIdsJson).ShouldBe([7]);
        row.RevisionPreference.ShouldBe(strategy.ToString());
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> operation)
    {
        try
        { await operation(); return null; }
        catch (Exception error) { return error; }
    }

    private sealed class ObservedStore(IConsumerUserSettingsStore inner) : IConsumerUserSettingsStore
    {
        public int Saves { get; private set; }
        public Action? AfterAcknowledgement { get; init; }
        public Exception? Fault { get; init; }
        public bool FailAfterWrite { get; init; }
        public Task<ConsumerAccountSettings> GetSettingsAsync(Guid userId, CancellationToken ct = default) => inner.GetSettingsAsync(userId, ct);
        public async Task<ConsumerAccountSettings> SaveSettingsAsync(Guid userId, ConsumerAccountSettings settings, CancellationToken ct = default)
        {
            Saves++;
            if (Fault is not null && !FailAfterWrite)
                throw Fault;
            var result = await inner.SaveSettingsAsync(userId, settings, ct);
            if (Fault is not null)
                throw Fault;
            AfterAcknowledgement?.Invoke();
            return result;
        }
    }

    private sealed class FailingResponseKeys(IConsumerTaxonomyKeys inner, Exception fault) : IConsumerTaxonomyKeys
    {
        public int CompletedResponseQueries { get; private set; }
        public Task<IReadOnlyDictionary<string, int>> RegionIdsAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default) => inner.RegionIdsAsync(keys, ct);
        public Task<IReadOnlyDictionary<string, int>> LanguageIdsAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default) => inner.LanguageIdsAsync(keys, ct);
        public Task<IReadOnlyDictionary<int, string>> LanguageKeysAsync(IReadOnlyCollection<int> ids, CancellationToken ct = default) => inner.LanguageKeysAsync(ids, ct);
        public async Task<IReadOnlyDictionary<int, string>> RegionKeysAsync(IReadOnlyCollection<int> ids, CancellationToken ct = default)
        {
            await inner.RegionKeysAsync(ids, ct);
            CompletedResponseQueries++;
            throw fault;
        }
    }

    private sealed class ReadProbe : DbCommandInterceptor
    {
        public bool Acknowledged { get; set; }
        public int PostAcknowledgementReads { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Acknowledged && (command.CommandText.Contains("\"Regions\"", StringComparison.Ordinal)
                    || command.CommandText.Contains("\"GameLanguages\"", StringComparison.Ordinal)))
            {
                PostAcknowledgementReads++;
                throw new IOException("Response query ran after the settings upsert acknowledgement.");
            }
            return ValueTask.FromResult(result);
        }
    }
}
