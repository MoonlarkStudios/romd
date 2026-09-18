using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Catalog;

public sealed class BiosGrouperTests
{
    private const int PlatformId = 3;

    private readonly IBiosRepository _biosRepository = Substitute.For<IBiosRepository>();

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
            .AddRangeAsync(Arg.Any<IReadOnlyList<Bios>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                createdInput = call.Arg<IReadOnlyList<Bios>>();
                return (IReadOnlyList<Bios>)createdInput
                    .Select((b, i) => Bios.Rehydrate(201 + i, b.PlatformId, b.Name, b.NormalizedName, b.CreatedAt))
                    .ToList();
            });

        IReadOnlyList<(int DatGameId, int BiosId)>? mappings = null;
        _biosRepository
            .When(r => r.AddGameMappingsAsync(Arg.Any<IReadOnlyList<(int, int)>>(), Arg.Any<CancellationToken>()))
            .Do(call => mappings = call.Arg<IReadOnlyList<(int DatGameId, int BiosId)>>());

        var grouper = new BiosGrouper(_biosRepository);

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

        mappings.ShouldNotBeNull();
        mappings.Count.ShouldBe(3);
        mappings.ShouldContain((101, 201));
        mappings.ShouldContain((102, 201));
        mappings.ShouldContain((103, 200));
    }

    [Fact]
    public async Task GroupAsync_NoGames_DoesNothing()
    {
        var grouper = new BiosGrouper(_biosRepository);

        var created = await grouper.GroupAsync(PlatformId, []);

        created.ShouldBe(0);
        await _biosRepository.DidNotReceive()
            .GetByNormalizedNamesAsync(Arg.Any<int>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        await _biosRepository.DidNotReceive().AddRangeAsync(Arg.Any<IReadOnlyList<Bios>>(), Arg.Any<CancellationToken>());
        await _biosRepository.DidNotReceive()
            .AddGameMappingsAsync(Arg.Any<IReadOnlyList<(int, int)>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GroupAsync_NamesThatNormalizeEmpty_AreSkipped()
    {
        _biosRepository
            .GetByNormalizedNamesAsync(PlatformId, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Bios>());
        _biosRepository
            .AddRangeAsync(Arg.Any<IReadOnlyList<Bios>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<Bios>)call.Arg<IReadOnlyList<Bios>>()
                .Select((b, i) => Bios.Rehydrate(300 + i, b.PlatformId, b.Name, b.NormalizedName, b.CreatedAt))
                .ToList());

        var grouper = new BiosGrouper(_biosRepository);

        var created = await grouper.GroupAsync(PlatformId, [(1, "[BIOS]"), (2, "[BIOS] Real (USA)")]);

        created.ShouldBe(1);
    }
}
