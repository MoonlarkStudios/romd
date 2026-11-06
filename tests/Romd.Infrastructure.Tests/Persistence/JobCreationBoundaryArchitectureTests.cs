using System.Reflection;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Jobs;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class JobCreationBoundaryArchitectureTests
{
    [Fact]
    public void CommonWorkerPorts_DoNotExposeJobCreation()
    {
        string[] readsAndCheckpoint = ["GetActiveAsync", "GetByIdAsync", "UpdateStagedAsync"];
        string[] claims = ["HasExecutionOwnershipAsync", "RenewExecutionLeaseAsync", "TryClaimExecutionAsync", "TryDeferClaimedAsync", "TryUpdateClaimedAsync"];
        typeof(IJobRepository<>).GetMethods().Select(method => method.Name).Order().ShouldBe(readsAndCheckpoint);
        typeof(IJobExecutionClaimRepository<>).GetMethods().Select(method => method.Name).Order().ShouldBe(claims);
        typeof(IJobExecutionClaimRepository<>).GetInterfaces().ShouldHaveSingleItem().GetGenericTypeDefinition()
            .ShouldBe(typeof(IJobRepository<>));
        typeof(ClaimedJobRepository<ExportJob, ExportJobRepository>)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(method => method.Name).Order().ShouldBe(readsAndCheckpoint.Concat(claims).Order());
    }

    [Fact]
    public void ExportAcceptance_ExposesOnlyStagedCreationAndLockRead()
    {
        typeof(IExportJobAcceptance).GetMethods().Select(method => method.Name).Order().ShouldBe(
            new[] { "AddStagedAsync", "LockAndHasPendingStagedAsync" });
        typeof(ExportJobRepository).GetMethod("AddAsync").ShouldBeNull();
    }

    [Fact]
    public void EnrichmentAcceptance_ExposesOnlyStagedCreationAndLockedPendingRead()
    {
        typeof(IEnrichmentJobAcceptance).GetMethods().Select(method => method.Name).Order().ShouldBe(
            new[] { "AddStagedAsync", "LockAndHasPendingForTitleStagedAsync" });
        typeof(IBulkEnrichmentJobAcceptance).GetMethods().Select(method => method.Name).Order().ShouldBe(
            new[] { "AddStagedAsync", "LockAndGetPendingForPlatformStagedAsync" });
        typeof(EnrichmentJobRepository).IsAssignableTo(typeof(IEnrichmentJobAcceptance)).ShouldBeTrue();
        typeof(BulkEnrichmentJobRepository).IsAssignableTo(typeof(IBulkEnrichmentJobAcceptance)).ShouldBeTrue();
        foreach (var portOrAdapter in new[]
        {
            typeof(IEnrichmentJobRepository), typeof(IBulkEnrichmentJobRepository),
            typeof(EnrichmentJobRepository), typeof(BulkEnrichmentJobRepository)
        })
            portOrAdapter.GetMethod("AddAsync").ShouldBeNull();
    }

    [Fact]
    public void RemainingFamilyCreation_IsExplicitOnMetadataRematerializationPort()
    {
        typeof(IMetadataRematerializationJobRepository).GetMethod("AddAsync").ShouldNotBeNull()
            .DeclaringType.ShouldBe(typeof(IMetadataRematerializationJobRepository));
    }

    [Fact]
    public void MaterializationAcceptance_RequiresStagedCreationWithoutARepositoryCommit()
    {
        typeof(IMaterializationJobRepository).GetMethod("AddAsync").ShouldBeNull();
        typeof(IMaterializationJobRepository).GetMethod("TryAddIfNoActiveForLibraryAsync").ShouldBeNull();
        typeof(MaterializationJobRepository).GetMethod("AddAsync").ShouldBeNull();
        typeof(MaterializationJobRepository).GetMethod("TryAddIfNoActiveForLibraryAsync").ShouldBeNull();
        typeof(IMaterializationJobRepository).GetMethod("AddStagedAsync").ShouldNotBeNull()
            .DeclaringType.ShouldBe(typeof(IMaterializationJobRepository));
    }
}
