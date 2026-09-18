using Microsoft.Extensions.Logging;
using NSubstitute;
using Romd.Application.Common.Configuration;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Jobs.Executors;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class ExportJobExecutorAuthorizationTests
{
    [Theory]
    [InlineData(null, null, null)]
    [InlineData(ExportScopeKind.Library, 7, null)]
    [InlineData(ExportScopeKind.Library, 7, -1L)]
    [InlineData(ExportScopeKind.Library, 0, 1L)]
    [InlineData(ExportScopeKind.AllCatalog, 7, 1L)]
    [InlineData(ExportScopeKind.Invalid, null, null)]
    public async Task ExecuteAsync_LegacyOrCorruptScope_RejectsBeforeCatalogRead(
        ExportScopeKind? scopeKind,
        int? libraryId,
        long? generation)
    {
        var repository = Substitute.For<IExportRepository>();
        var executor = new ExportJobExecutor(
            repository,
            Substitute.For<IFileStorageService>(),
            Substitute.For<IRomdOptions>(),
            Substitute.For<ILogger<ExportJobExecutor>>());
        var job = Rehydrate(scopeKind, libraryId, generation);
        var context = new JobContext(null, _ => Task.CompletedTask, CancellationToken.None);

        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => executor.ExecuteAsync(job, context));

        error.Message.ShouldContain("invalid or legacy authorization scope");
        await repository.DidNotReceiveWithAnyArgs().GetExportFilesAsync(default!, default);
    }

    private static ExportJob Rehydrate(
        ExportScopeKind? scopeKind,
        int? libraryId,
        long? generation) =>
        ExportJob.Rehydrate(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Library Export",
            platformId: null,
            ExportPhase.Collecting,
            hangfireJobId: "delivery",
            scopeKind,
            libraryId,
            generation,
            totalTitles: 0,
            processedTitles: 0,
            totalFiles: 0,
            processedFiles: 0,
            skippedFiles: 0,
            exportPath: null,
            currentItem: null,
            errors: [],
            createdAt: DateTimeOffset.UtcNow,
            startedAt: DateTimeOffset.UtcNow,
            completedAt: null,
            isArchived: false,
            archivedAt: null,
            createdByUserId: Guid.NewGuid());
}
