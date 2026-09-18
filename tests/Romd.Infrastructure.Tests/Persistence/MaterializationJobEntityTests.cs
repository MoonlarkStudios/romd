using Romd.Domain.Jobs;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class MaterializationJobEntityTests
{
    [Fact]
    public void FromDomain_ToDomain_RoundTripsDeferredPhase()
    {
        var domain = MaterializationJob.Create(7, "Arcade");
        domain.Start("hangfire-1");
        domain.MarkDeferred();
        domain.Complete();

        var entity = MaterializationJobEntity.FromDomain(domain);
        entity.Phase.ShouldBe("Deferred");
        entity.JobType.ShouldBe("materialization");

        var roundTripped = entity.ToDomain();
        roundTripped.PhaseEnum.ShouldBe(MaterializationPhase.Deferred);
        roundTripped.IsTerminal.ShouldBeTrue();
        roundTripped.HasErrors.ShouldBeFalse();
        roundTripped.TotalTitles.ShouldBe(0);
        roundTripped.ProcessedCount.ShouldBe(0);
    }
}
