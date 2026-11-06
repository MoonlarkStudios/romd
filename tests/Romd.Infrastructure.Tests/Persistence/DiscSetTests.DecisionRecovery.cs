using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Catalog.Commands.DecideDiscSet;
using Romd.Application.Common.Concurrency;
using Romd.Domain.Libraries;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Catalog;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class DiscSetTests
{
    [Theory]
    [InlineData("none", false)]
    [InlineData("before", false)]
    [InlineData("after", false)]
    [InlineData("before", true)]
    [InlineData("after", true)]
    public async Task Decision_RealCommitAndDisposalFaults_PreserveAcknowledgementAndAtomicLibraryEffects(string fault, bool cancel)
    {
        await Refresh();
        var original = await First();
        int id = DecodeId(original.Id);
        Dictionary<int, Guid> libraryRevisions;
        await using (var seed = database.CreateContext())
        {
            await seed.Libraries.Where(row => row.Id == 1).ExecuteUpdateAsync(update => update.SetProperty(row => row.ConfigurationJson,
                JsonSerializer.Serialize(new LibraryConfiguration { SelectedDiscSetIds = [id] })));
            seed.Libraries.AddRange(new LibraryEntity
            {
                Id = 2,
                Name = "Unrelated",
                ConfigurationJson = "{}",
                ConfigurationState = "Valid",
                MaterializationRevision = Guid.NewGuid(),
                NeedsMaterialization = false
            }, new LibraryEntity
            {
                Id = 3,
                Name = "Published",
                ConfigurationJson = "{}",
                ConfigurationState = "Valid",
                MaterializationRevision = Guid.NewGuid(),
                NeedsMaterialization = false
            });
            seed.MaterializedLibraryDiscSets.Add(new MaterializedLibraryDiscSetEntity
            {
                LibraryId = 3,
                DiscSetId = id,
                PlatformId = 1,
                TitleId = 1,
                Revision = original.Revision,
                DefinitionJson = JsonSerializer.Serialize(new PublishedDiscSetDefinition("Published", 2,
                    [new(1, "sha1:1", 1, 1), new(2, "sha1:2", 2, 1)]))
            });
            await seed.SaveChangesAsync();
            libraryRevisions = await seed.Libraries.ToDictionaryAsync(row => row.Id, row => row.MaterializationRevision);
        }
        using var cancellation = new CancellationTokenSource();
        Exception primary = cancel ? new OperationCanceledException("Original decision interruption.", cancellation.Token)
            : new IOException("Original decision acknowledgement failure.");
        await using var db = new RomdDbContext(database.CreateOptions(options =>
            options.ReplaceService<IRelationalTransactionFactory, ProviderTransactionFaults.Factory>()));
        var faults = new ProviderTransactionFaults(fault, primary, cancel ? cancellation.Cancel : null);
        faults.Register(db);
        var time = DateTimeOffset.UnixEpoch.AddTicks(1234567);
        var handler = new DecideDiscSetCommandHandler(new DiscSetService(db), new EfUnitOfWork(db), new DecisionClock(time),
            NullLogger<DecideDiscSetCommandHandler>.Instance);
        var command = new DecideDiscSetCommand(id, new("accept", null, null, null, "Reviewed"),
            RevisionPrecondition.For(original.Revision.ToString()), actor);

        if (fault == "none")
        {
            var result = await handler.HandleAsync(command, cancellation.Token);
            result.IsError.ShouldBeFalse();
            result.Value.Decision.ShouldBe("accepted");
            result.Value.Revision.ShouldNotBe(original.Revision);
            result.Value.DecidedBy.ShouldBe(actor);
            result.Value.DecidedAt.ShouldBe(time);
        }
        else
        {
            Exception? observed = null;
            try
            { await handler.HandleAsync(command, cancellation.Token); }
            catch (Exception error) { observed = error; }
            observed.ShouldBeSameAs(primary);
            if (observed is OperationCanceledException cancelled)
                cancelled.CancellationToken.ShouldBe(cancellation.Token);
        }
        faults.Disposals.ShouldBe(1);
        faults.Commits.ShouldBe(fault == "before" ? 0 : 1);
        db.Database.CurrentTransaction.ShouldBeNull();
        await db.SaveChangesAsync();
        await using var verify = database.CreateContext();
        var stored = await verify.DiscSets.SingleAsync(row => row.Id == id);
        bool durable = fault != "before";
        stored.Decision.ShouldBe(durable ? "accepted" : "proposed");
        if (durable)
        {
            stored.Revision.ShouldNotBe(original.Revision);
            stored.CorrectionJson.ShouldNotBeNull();
            stored.DecidedAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(time.ToUnixTimeMilliseconds()));
            stored.DecidedBy.ShouldBe(actor);
            stored.Reason.ShouldBe("Reviewed");
        }
        else
        {
            stored.Revision.ShouldBe(original.Revision);
            stored.CorrectionJson.ShouldBeNull();
            stored.DecidedBy.ShouldBeNull();
            stored.DecidedAt.ShouldBeNull();
        }
        var libraries = await verify.Libraries.OrderBy(row => row.Id).ToListAsync();
        libraries.Select(row => row.NeedsMaterialization).ShouldBe([durable, false, durable]);
        foreach (var library in libraries)
        {
            if (durable && library.Id != 2)
                library.MaterializationRevision.ShouldNotBe(libraryRevisions[library.Id]);
            else
                library.MaterializationRevision.ShouldBe(libraryRevisions[library.Id]);
        }
    }

    [Fact]
    public async Task Decision_RepeatedReject_AlwaysAdvancesRevisionAndPreservesStoredCorrectionBytes()
    {
        await Refresh();
        var current = await Decide(await First(), "accept", publish: false);
        string correction;
        await using (var seed = database.CreateContext())
        {
            var row = await seed.DiscSets.SingleAsync();
            correction = "  " + row.CorrectionJson + "  ";
            await seed.DiscSets.ExecuteUpdateAsync(update => update.SetProperty(row => row.CorrectionJson, correction));
        }
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var previous = current.Revision;
            current = await Decide(current, "reject", publish: false);
            current.Decision.ShouldBe("rejected");
            current.Revision.ShouldNotBe(previous);
            await using var verify = database.CreateContext();
            (await verify.DiscSets.SingleAsync()).CorrectionJson.ShouldBe(correction);
        }
    }

    private sealed class DecisionClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
