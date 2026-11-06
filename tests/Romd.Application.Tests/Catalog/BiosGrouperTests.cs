using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Catalog;

public sealed class BiosGrouperTests
{
    private const int PlatformId = 3;

    private readonly IBiosRepository _biosRepository = Substitute.For<IBiosRepository>();
    private readonly IUnitOfWork _work = Substitute.For<IUnitOfWork>();

    [Fact]
    public async Task GroupAsync_CreatesMissingAndMatchesExisting()
    {
        // Two USA games (same firmware) → one new entry, two mappings.
        // Japan already grouped by a prior DAT → matches existing, no create.
        _biosRepository
            .GetByNormalizedNamesAsync(PlatformId, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Bios>
            {
                ["psxjapan"] = Bios.Rehydrate(200, PlatformId, "[BIOS] PSX (Japan)", "psxjapan", DateTimeOffset.UtcNow)
            });

        IReadOnlyList<Bios>? createdInput = null;
        _biosRepository
            .AddRangeStagedAsync(Arg.Any<IReadOnlyList<Bios>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                createdInput = call.Arg<IReadOnlyList<Bios>>();
                return Receipt(createdInput, 201);
            });

        IReadOnlyList<(int DatGameId, int BiosId)>? mappings = null;
        _biosRepository
            .When(r => r.AddGameMappingsStagedAsync(Arg.Any<IReadOnlyList<(int, int)>>(), Arg.Any<CancellationToken>()))
            .Do(call => mappings = call.Arg<IReadOnlyList<(int DatGameId, int BiosId)>>());

        var publishedAt = DateTimeOffset.UnixEpoch.AddTicks(1234567);
        var grouper = new BiosGrouper(_biosRepository, _work, clock: new FixedClock(publishedAt));

        var created = await grouper.GroupAsync(PlatformId,
        [
            (101, "[BIOS] PSX (USA)"),
            (102, "[BIOS] PSX (USA)"),
            (103, "[BIOS] PSX (Japan)")
        ]);

        created.ShouldBe(1);

        createdInput.ShouldNotBeNull();
        createdInput.Count.ShouldBe(1);
        createdInput[0].NormalizedName.ShouldBe("psxusa");
        createdInput[0].Name.ShouldBe("[BIOS] PSX (USA)");
        createdInput[0].PlatformId.ShouldBe(PlatformId);
        createdInput[0].CreatedAt.ShouldBe(publishedAt);

        mappings.ShouldNotBeNull();
        mappings.Count.ShouldBe(3);
        mappings.ShouldContain((101, 201));
        mappings.ShouldContain((102, 201));
        mappings.ShouldContain((103, 200));
    }

    [Fact]
    public async Task GroupAsync_NoGames_DoesNothing()
    {
        var grouper = new BiosGrouper(_biosRepository, _work, clock: TimeProvider.System);

        var created = await grouper.GroupAsync(PlatformId, []);

        created.ShouldBe(0);
        await _biosRepository.DidNotReceive()
            .GetByNormalizedNamesAsync(Arg.Any<int>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        await _biosRepository.DidNotReceive().AddRangeStagedAsync(Arg.Any<IReadOnlyList<Bios>>(), Arg.Any<CancellationToken>());
        await _biosRepository.DidNotReceive()
            .AddGameMappingsStagedAsync(Arg.Any<IReadOnlyList<(int, int)>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GroupAsync_NamesThatNormalizeEmpty_AreSkipped()
    {
        _biosRepository
            .GetByNormalizedNamesAsync(PlatformId, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Bios>());
        _biosRepository
            .AddRangeStagedAsync(Arg.Any<IReadOnlyList<Bios>>(), Arg.Any<CancellationToken>())
            .Returns(call => Receipt(call.Arg<IReadOnlyList<Bios>>(), 300));

        var grouper = new BiosGrouper(_biosRepository, _work, clock: TimeProvider.System);

        var created = await grouper.GroupAsync(PlatformId, [(1, "[BIOS]"), (2, "[BIOS] Real (USA)")]);

        created.ShouldBe(1);
    }

    [Fact]
    public async Task GroupAsync_NewEntries_FlushesBeforeReadingIdsAndStagingMappings()
    {
        var order = new List<string>();
        _biosRepository.GetByNormalizedNamesAsync(PlatformId, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Bios>());
        var receipt = Substitute.For<IStagedBiosCreationBatch>();
        receipt.ReadAfterFlush().Returns(_ =>
        {
            order.Add("read-identities");
            return new[] { Bios.Rehydrate(42, PlatformId, "[BIOS] PSX", "psx", DateTimeOffset.UnixEpoch) };
        });
        _biosRepository.AddRangeStagedAsync(Arg.Any<IReadOnlyList<Bios>>(), Arg.Any<CancellationToken>())
            .Returns(_ => { order.Add("stage-creation"); return receipt; });
        _work.When(work => work.FlushAsync(Arg.Any<CancellationToken>())).Do(_ => order.Add("flush"));
        _biosRepository.When(repo => repo.AddGameMappingsStagedAsync(
            Arg.Any<IReadOnlyList<(int, int)>>(), Arg.Any<CancellationToken>())).Do(_ => order.Add("stage-mappings"));

        (await new BiosGrouper(_biosRepository, _work, TimeProvider.System).GroupAsync(PlatformId, [(1, "[BIOS] PSX")])).ShouldBe(1);

        order.ShouldBe(["stage-creation", "flush", "read-identities", "stage-mappings"]);
        await _work.DidNotReceive().BeginTransactionAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GroupAsync_AllMatched_StagesMappingsWithoutCreationFlush()
    {
        _biosRepository.GetByNormalizedNamesAsync(PlatformId, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Bios> { ["psx"] = Bios.Rehydrate(42, PlatformId, "First display", "psx", DateTimeOffset.UnixEpoch) });

        var created = await new BiosGrouper(_biosRepository, _work, TimeProvider.System).GroupAsync(PlatformId, [(1, "[BIOS] PSX")]);

        created.ShouldBe(0);
        await _biosRepository.DidNotReceive().AddRangeStagedAsync(Arg.Any<IReadOnlyList<Bios>>(), Arg.Any<CancellationToken>());
        await _work.DidNotReceive().FlushAsync(Arg.Any<CancellationToken>());
        await _biosRepository.Received(1).AddGameMappingsStagedAsync(
            Arg.Is<IReadOnlyList<(int DatGameId, int BiosId)>>(values => values.Count == 1 && values[0].DatGameId == 1 && values[0].BiosId == 42),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GroupAsync_CreationFlushFails_DoesNotStageMappings()
    {
        _biosRepository.GetByNormalizedNamesAsync(PlatformId, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Bios>());
        _biosRepository.AddRangeStagedAsync(Arg.Any<IReadOnlyList<Bios>>(), Arg.Any<CancellationToken>())
            .Returns(call => Receipt(call.Arg<IReadOnlyList<Bios>>(), 300));
        _work.FlushAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("Injected creation flush failure.")));

        await Should.ThrowAsync<IOException>(() =>
            new BiosGrouper(_biosRepository, _work, TimeProvider.System).GroupAsync(PlatformId, [(1, "[BIOS] PSX")]));

        await _biosRepository.DidNotReceive().AddGameMappingsStagedAsync(Arg.Any<IReadOnlyList<(int, int)>>(), Arg.Any<CancellationToken>());
    }

    private static IStagedBiosCreationBatch Receipt(IReadOnlyList<Bios> entries, int firstId)
    {
        var receipt = Substitute.For<IStagedBiosCreationBatch>();
        receipt.ReadAfterFlush().Returns(entries.Select((entry, i) => Bios.Rehydrate(firstId + i,
            entry.PlatformId, entry.Name, entry.NormalizedName, entry.CreatedAt)).ToList());
        return receipt;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
