using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Systems;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class JobNextAttemptMappingTests
{
    private static readonly SystemKeys Systems = new(new Dictionary<int, string> { [1] = "snes" });

    public static TheoryData<string> JobTypes => new()
    {
        "upload", "replace_dat", "enrichment", "bulk_enrichment", "artwork-completion", "export", "materialization", "artwork-import"
    };

    [Theory]
    [MemberData(nameof(JobTypes))]
    public void ToContract_PersistedDeferral_ExposesNextAttemptForEveryJobType(string jobType)
    {
        var due = new DateTimeOffset(2026, 9, 22, 14, 32, 0, TimeSpan.Zero);
        var deferred = Entity(jobType);
        deferred.NextAttemptAtUtc = due;

        var scheduled = ToDomain(deferred).ToContract(Systems);
        var undeferred = ToDomain(Entity(jobType)).ToContract(Systems);

        scheduled.NextAttemptAt.ShouldBe(due);
        undeferred.NextAttemptAt.ShouldBeNull();
    }

    private static JobEntity Entity(string jobType) => jobType switch
    {
        "upload" => UploadJobEntity.FromDomain(UploadJob.Create("archive.zip", createdAt: DateTimeOffset.UtcNow)),
        "replace_dat" => ReplaceDatJobEntity.FromDomain(ReplaceDatJob.Create(1, "catalog.dat", createdAt: DateTimeOffset.UtcNow)),
        "enrichment" => EnrichmentJobEntity.FromDomain(EnrichmentJob.Create("Title", 1, 1, TimeProvider.System.GetUtcNow())),
        "bulk_enrichment" => BulkEnrichmentJobEntity.FromDomain(BulkEnrichmentJob.Create(1, "SNES", createdAt: DateTimeOffset.UtcNow)),
        "artwork-completion" => ArtworkCompletionJobEntity.FromDomain(ArtworkCompletionJob.Create(Guid.NewGuid(), 1, null, createdAt: DateTimeOffset.UtcNow)),
        "export" => ExportJobEntity.FromDomain(ExportJob.CreateAllCatalog(createdAt: DateTimeOffset.UtcNow)),
        "materialization" => MaterializationJobEntity.FromDomain(MaterializationJob.Create(1, "Library", createdAt: DateTimeOffset.UtcNow)),
        "artwork-import" => ArtworkImportJobEntity.FromDomain(ArtworkImportJob.Create(Guid.NewGuid(), "Title", 1, 1, ArtworkRole.Poster,
            1, "igdb", "42", "asset", "https://example.com/image", null, TimeProvider.System.GetUtcNow())),
        _ => throw new ArgumentOutOfRangeException(nameof(jobType), jobType, "Unknown job type.")
    };

    private static Job ToDomain(JobEntity entity) => entity switch
    {
        UploadJobEntity upload => upload.ToDomain(),
        ReplaceDatJobEntity replace => replace.ToDomain(),
        EnrichmentJobEntity enrichment => enrichment.ToDomain(),
        BulkEnrichmentJobEntity bulk => bulk.ToDomain(),
        ArtworkCompletionJobEntity completion => completion.ToDomain(),
        ExportJobEntity export => export.ToDomain(),
        MaterializationJobEntity materialization => materialization.ToDomain(),
        ArtworkImportJobEntity artwork => artwork.ToDomain(),
        _ => throw new ArgumentOutOfRangeException(nameof(entity), entity.JobType, "Unknown job entity.")
    };
}
