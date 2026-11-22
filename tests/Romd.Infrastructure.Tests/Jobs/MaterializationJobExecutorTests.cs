using Microsoft.Extensions.Logging;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Domain.Jobs;
using Romd.Domain.Libraries;
using Romd.Infrastructure.Jobs.Executors;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class MaterializationJobExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_CheckpointsCurrentItemBeforeDelegatingToPublicationOwner()
    {
        var service = Substitute.For<ILibraryMaterializationService>();
        var libraries = Substitute.For<ILibraryRepository>();
        var library = Library.Rehydrate(7, "Arcade renamed", new LibraryConfiguration(), LibraryConfigurationState.Valid,
            null, false, true, null, 0, DateTimeOffset.UtcNow, null);
        libraries.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(library);
        var job = MaterializationJob.Create(7, "Arcade", DateTimeOffset.UtcNow);
        job.Start("delivery", DateTimeOffset.UtcNow);
        int checkpoints = 0;
        var context = new JobContext(null, _ =>
        {
            job.CurrentItem.ShouldBe(library.Name);
            checkpoints++;
            return Task.CompletedTask;
        }, default);
        service.PublishForJobAsync(job, context.CancellationToken).Returns(_ =>
        {
            checkpoints.ShouldBe(1);
            return Task.CompletedTask;
        });
        var executor = new MaterializationJobExecutor(service, libraries,
            Substitute.For<ILogger<MaterializationJobExecutor>>());

        await executor.ExecuteAsync(job, context);

        await service.Received(1).PublishForJobAsync(job, context.CancellationToken);
        checkpoints.ShouldBe(1);
    }
}
