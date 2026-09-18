using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public class BulkEnrichmentJobEntityTests
{
    [Fact]
    public void FromDomain_ToDomain_RoundTrip()
    {
        var domain = BulkEnrichmentJob.Create(platformId: 42, platformName: "SNES", scope: EnrichmentScope.All);
        domain.Start("hangfire-123");
        domain.SetTotalTitles(100);
        domain.RecordEnriched();
        domain.RecordEnriched();
        domain.RecordNotFound();
        domain.RecordSkipped();
        domain.RecordFailed(7, "Bad Game", "API timeout", JobErrorReason.ProviderError);

        // FromDomain
        var entity = BulkEnrichmentJobEntity.FromDomain(domain);

        entity.Id.ShouldBe(domain.Id);
        entity.CorrelationId.ShouldBe(domain.CorrelationId);
        entity.PlatformId.ShouldBe(42);
        entity.Phase.ShouldBe("Enriching");
        entity.HangfireJobId.ShouldBe("hangfire-123");
        entity.TotalTitles.ShouldBe(100);
        entity.ProcessedCount.ShouldBe(5);
        entity.EnrichedCount.ShouldBe(2);
        entity.NotFoundCount.ShouldBe(1);
        entity.FailedCount.ShouldBe(1);
        entity.SkippedCount.ShouldBe(1);
        entity.Scope.ShouldBe("All");
        entity.JobType.ShouldBe("bulk_enrichment");

        // ToDomain round-trip
        var roundTripped = entity.ToDomain();

        roundTripped.Id.ShouldBe(domain.Id);
        roundTripped.CorrelationId.ShouldBe(domain.CorrelationId);
        roundTripped.PlatformId.ShouldBe(42);
        roundTripped.Phase.ShouldBe("Enriching");
        roundTripped.HangfireJobId.ShouldBe("hangfire-123");
        roundTripped.TotalTitles.ShouldBe(100);
        roundTripped.ProcessedCount.ShouldBe(5);
        roundTripped.EnrichedCount.ShouldBe(2);
        roundTripped.NotFoundCount.ShouldBe(1);
        roundTripped.FailedCount.ShouldBe(1);
        roundTripped.SkippedCount.ShouldBe(1);
        roundTripped.Scope.ShouldBe(EnrichmentScope.All);

        // Errors round-trip — including the new fields stored in the JSON blob.
        roundTripped.Errors.Count.ShouldBe(1);
        roundTripped.Errors[0].Item.ShouldBe("Bad Game");
        roundTripped.Errors[0].Message.ShouldBe("API timeout");
        roundTripped.Errors[0].EntityId.ShouldBe(7);
        roundTripped.Errors[0].Reason.ShouldBe(JobErrorReason.ProviderError);
    }
}
