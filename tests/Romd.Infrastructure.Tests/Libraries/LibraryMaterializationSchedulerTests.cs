using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Domain.Jobs;
using Romd.Domain.Libraries;
using Romd.Infrastructure.Libraries;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Libraries;

public sealed class LibraryMaterializationSchedulerTests
{
    [Fact]
    public async Task EnqueueIfNeededAsync_FlaggedLibrary_CreatesMaterializationQueueJob()
    {
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var jobRepo = Substitute.For<IMaterializationJobRepository>();
        var scheduler = new LibraryMaterializationScheduler(
            libraryRepo,
            jobRepo,
            NullLogger<LibraryMaterializationScheduler>.Instance);

        libraryRepo.GetByIdAsync(7, Arg.Any<CancellationToken>())
            .Returns(Library.CreateNew("Arcade", new LibraryConfiguration()));
        jobRepo.TryAddIfNoActiveForLibraryAsync(Arg.Any<MaterializationJob>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var enqueued = await scheduler.EnqueueIfNeededAsync(7);

        enqueued.ShouldBeTrue();
        await jobRepo.Received(1)
            .TryAddIfNoActiveForLibraryAsync(
                Arg.Is<MaterializationJob>(job => job.LibraryId == 7),
                Arg.Any<CancellationToken>());
        await jobRepo.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    [Fact]
    public async Task EnqueueIfNeededAsync_LibraryNotFlagged_DoesNotCreateJob()
    {
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var jobRepo = Substitute.For<IMaterializationJobRepository>();
        var scheduler = new LibraryMaterializationScheduler(
            libraryRepo,
            jobRepo,
            NullLogger<LibraryMaterializationScheduler>.Instance);
        var library = Library.CreateNew("Arcade", new LibraryConfiguration());
        library.MarkMaterialized(1);

        libraryRepo.GetByIdAsync(7, Arg.Any<CancellationToken>())
            .Returns(library);

        var enqueued = await scheduler.EnqueueIfNeededAsync(7);

        enqueued.ShouldBeFalse();
        await jobRepo.DidNotReceiveWithAnyArgs().TryAddIfNoActiveForLibraryAsync(default!, default);
    }

    [Fact]
    public async Task EnqueueIfNeededAsync_ActiveMaterializationJob_DoesNotCreateDuplicate()
    {
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var jobRepo = Substitute.For<IMaterializationJobRepository>();
        var scheduler = new LibraryMaterializationScheduler(
            libraryRepo,
            jobRepo,
            NullLogger<LibraryMaterializationScheduler>.Instance);

        libraryRepo.GetByIdAsync(7, Arg.Any<CancellationToken>())
            .Returns(Library.CreateNew("Arcade", new LibraryConfiguration()));
        jobRepo.TryAddIfNoActiveForLibraryAsync(Arg.Any<MaterializationJob>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var enqueued = await scheduler.EnqueueIfNeededAsync(7);

        enqueued.ShouldBeFalse();
        await jobRepo.Received(1)
            .TryAddIfNoActiveForLibraryAsync(
                Arg.Is<MaterializationJob>(job => job.LibraryId == 7),
                Arg.Any<CancellationToken>());
    }
}
