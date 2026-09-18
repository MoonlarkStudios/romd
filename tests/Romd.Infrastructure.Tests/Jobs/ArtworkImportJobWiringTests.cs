using System.Reflection;
using System.Text.Json;
using Hangfire;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Models;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Jobs.Handlers;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class ArtworkImportJobWiringTests
{
    [Fact]
    public void CreateInvocation_ArtworkImport_UsesTypedHandlerAndStableRequestId()
    {
        var requestId = Guid.NewGuid();

        var (invocation, queue) = JobDispatchService.CreateInvocation("artwork-import", requestId);

        invocation.Type.ShouldBe(typeof(ArtworkImportJobHangfireHandler));
        invocation.Method.Name.ShouldBe(nameof(ArtworkImportJobHangfireHandler.ExecuteAsync));
        invocation.Args[0].ShouldBe(requestId);
        queue.ShouldBe(JobQueues.Enrichment);
        var queueAttribute = invocation.Method.GetCustomAttribute<QueueAttribute>();
        queueAttribute.ShouldNotBeNull();
        queueAttribute.Queue.ShouldBe(queue);
        var retry = invocation.Method.GetCustomAttribute<AutomaticRetryAttribute>();
        retry.ShouldNotBeNull();
        retry.Attempts.ShouldBe(2);
        retry.OnAttemptsExceeded.ShouldBe(AttemptsExceededAction.Fail);
    }

    [Fact]
    public void ToContract_CompletedArtworkImport_ExposesStatusAndEncodedOutcome()
    {
        var job = Create();
        job.Start("delivery-1");
        job.SetImportOutcome(123, false);
        job.Complete();

        var contract = ((Romd.Domain.Jobs.Job)job).ToContract(new Romd.Application.Common.Systems.SystemKeys(new Dictionary<int, string> { [2] = "snes" })).ShouldBeOfType<ArtworkImportJobDto>();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize<JobDto>(contract,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        contract.Id.ShouldBe(job.Id);
        contract.TitleId.ShouldBe(IdCoder.Encode(7));
        contract.SystemKey.ShouldBe("snes");
        contract.Role.ShouldBe("Poster");
        contract.ProviderId.ShouldBe("steamgriddb");
        contract.RetainedAssetId.ShouldBe(IdCoder.Encode(123));
        contract.IsTerminal.ShouldBeTrue();
        contract.Phase.ShouldBe("Completed");
        contract.WasSuperseded.ShouldBeFalse();
        json.RootElement.GetProperty("jobType").GetString().ShouldBe("artwork-import");
        json.RootElement.TryGetProperty("providerGameId", out _).ShouldBeFalse();
        json.RootElement.TryGetProperty("providerAssetId", out _).ShouldBeFalse();
        json.RootElement.TryGetProperty("trustedAssetUrl", out _).ShouldBeFalse();
    }

    [Fact]
    public void ToContract_SupersededBeforeDownload_PreservesNoAssetOutcome()
    {
        var job = Create();
        job.Start("delivery-1");
        job.SetImportOutcome(null, true);
        job.Complete();

        var contract = job.ToContract(new Romd.Application.Common.Systems.SystemKeys(new Dictionary<int, string> { [2] = "snes" }));

        contract.IsTerminal.ShouldBeTrue();
        contract.WasSuperseded.ShouldBeTrue();
        contract.RetainedAssetId.ShouldBeNull();
        contract.HasErrors.ShouldBeFalse();
    }

    [Fact]
    public void ToDomain_EntityRoundTrip_PreservesPendingFenceIdentityAndErrorHistory()
    {
        var job = Create();
        job.Start("delivery-1");
        job.Fail("Invalid image.");
        job.Archive();
        var entity = ArtworkImportJobEntity.FromDomain(job);

        var restored = entity.ToDomain();

        entity.JobType.ShouldBe("artwork-import");
        restored.Id.ShouldBe(job.Id);
        restored.SelectionRevision.ShouldBe(4);
        restored.ProviderGameId.ShouldBe("game-1");
        restored.ProviderAssetId.ShouldBe("asset-1");
        restored.TrustedAssetUrl.ShouldBe(job.TrustedAssetUrl);
        restored.Attribution.ShouldBe("Artist");
        restored.PhaseEnum.ShouldBe(ArtworkImportPhase.Failed);
        restored.Errors.Single().Message.ShouldBe("Invalid image.");
        restored.IsArchived.ShouldBeTrue();
        restored.ArchivedAt.ShouldBe(job.ArchivedAt);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("99")]
    public void ToDomain_UnknownPersistedPhase_DoesNotRestartImport(string phase)
    {
        var entity = ArtworkImportJobEntity.FromDomain(Create());
        entity.Phase = phase;

        Should.Throw<InvalidOperationException>(() => entity.ToDomain());
    }

    private static ArtworkImportJob Create() => ArtworkImportJob.Create(Guid.NewGuid(), "Title", 7, 2,
        ArtworkRole.Poster, 4, "steamgriddb", "game-1", "asset-1", "https://cdn2.steamgriddb.com/grid/asset.png", "Artist", TimeProvider.System);
}
