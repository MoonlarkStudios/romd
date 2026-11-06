using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog;
using Romd.Domain.Catalog;
using Romd.Infrastructure.Source;
using Romd.Persistence;
using Romd.Persistence.Catalog;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class DiscSetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshStaged_RepeatedBeforeFlush_UpdatesOnePendingProposalWithoutFlushingUnrelatedState(bool unavailable)
    {
        await using var db = database.CreateContext();
        IDiscSetProposalStore proposals = new DiscSetService(db);
        var desired = DiscSetProposalBuilder.Create(await proposals.ReadClaimsAsync(1, default));
        desired.ShouldHaveSingleItem();
        var work = new EfUnitOfWork(db);
        await using var transaction = await work.BeginTransactionAsync();
        var platform = await db.Platforms.AsTracking().SingleAsync(row => row.Id == 1);
        platform.Name = "Must stay staged";

        await proposals.RefreshStagedAsync(1, desired, default);
        await proposals.RefreshStagedAsync(1, desired.Select(proposal => proposal with
        { Value = proposal.Value with { Name = "Latest proposal" } }).ToArray(), default);
        if (unavailable)
            await proposals.RefreshStagedAsync(1, [], default);

        db.ChangeTracker.Entries<DiscSetEntity>().ShouldHaveSingleItem().State.ShouldBe(EntityState.Added);
        (await db.DiscSets.CountAsync()).ShouldBe(0);
        (await db.Platforms.AsNoTracking().SingleAsync(row => row.Id == 1)).Name.ShouldBe("PlayStation");
        await work.FlushAsync();
        var row = await db.DiscSets.AsNoTracking().SingleAsync();
        row.Id.ShouldBeGreaterThan(0);
        var definition = DiscSetService.Decode(row.ProposalJson);
        definition.Name.ShouldBe("Latest proposal");
        definition.ReviewReasons.ShouldBe(unavailable ? ["SourceUnavailable"] : []);
        row.Decision.ShouldBe("proposed");
        row.CorrectionJson.ShouldBeNull();
        await using (var observer = database.CreateContext())
            (await observer.DiscSets.CountAsync()).ShouldBe(0);
        await transaction.RollbackAsync();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await work.FlushAsync();
        (await db.DiscSets.CountAsync()).ShouldBe(0);
        (await db.Platforms.AsNoTracking().SingleAsync(row => row.Id == 1)).Name.ShouldBe("PlayStation");
    }

    [Fact]
    public async Task RefreshStaged_RequiresCallerTransactionAndPropagatesCancellationBeforeTracking()
    {
        await using var db = database.CreateContext();
        IDiscSetProposalStore proposals = new DiscSetService(db);
        var desired = DiscSetProposalBuilder.Create(await proposals.ReadClaimsAsync(1, default));
        await Should.ThrowAsync<InvalidOperationException>(() => proposals.RefreshStagedAsync(1, desired, default));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        (await Should.ThrowAsync<OperationCanceledException>(() => proposals.RefreshStagedAsync(1, desired, cancellation.Token)))
            .CancellationToken.ShouldBe(cancellation.Token);
        db.ChangeTracker.Entries().ShouldBeEmpty();
        (await db.DiscSets.CountAsync()).ShouldBe(0);
    }
}
