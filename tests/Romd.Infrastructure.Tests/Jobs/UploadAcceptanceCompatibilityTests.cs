using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Classification;
using Romd.Admin.Application.Ingestion.Commands.AcceptUpload;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Admin.Application.Titles;
using Romd.Application.Common.Security;
using ErrorOr;
using Romd.Application.Common.Configuration;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Tests.Helpers;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class UploadAcceptanceCompatibilityTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("romd-upload-acceptance-").FullName;
    private readonly IUploadJobAcceptance _uploads = Substitute.For<IUploadJobAcceptance>();
    private readonly IJobRepository _jobs = Substitute.For<IJobRepository>();

    private Task<ErrorOr<Guid>> AcceptAsync(Stream stream, string fileName, UploadJobOptions settings, TimeProvider? clock = null)
    {
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(_directory);
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(settings.CreatedByUserId);
        var tracked = Substitute.For<ITrackedTitleReader>();
        tracked.HasTrackedAsync(null, Arg.Any<CancellationToken>()).Returns(true);
        var work = Substitute.For<IUnitOfWork>();
        var transaction = Substitute.For<ITransaction>();
        work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        var owner = new AcceptUploadCommandHandler(tracked, Substitute.For<IFileClassifier>(), _jobs,
            new JobWorkspaceExclusion(options), new UploadWorkspacePreparation(options), _uploads,
            work, user, options, clock ?? TimeProvider.System, NullLogger<AcceptUploadCommandHandler>.Instance);
        return owner.HandleAsync(new(UploadKind.Generic, new Input(stream, fileName), settings.PlatformId,
            settings.MaxParallelRoms, settings.AllowUnidentified, settings.ArchiveOnly, settings.TrackedOnly,
            settings.RequestId, settings.BatchId));
    }

    private sealed class Input(Stream source, string filename) : IUploadInput
    {
        public string FileName => filename;
        // Metadata and stream capacity are separate defenses: the oversized-stream case still
        // exercises preparation's free-space rejection after the HTTP metadata check succeeds.
        public long Length => 3;
        public Stream OpenRead() => source;
    }

    [Fact]
    public async Task Handle_InjectedClock_PersistsItsExactAcceptanceTime()
    {
        var at = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(1234560);
        using var stream = new MemoryStream([1, 2, 3]);

        var result = await AcceptAsync(stream, "pack.zip", new UploadJobOptions(), new ManualTimeProvider(at));

        result.IsError.ShouldBeFalse();
        await _uploads.Received(1).AddStagedAsync(Arg.Is<UploadJob>(job => job.Id == result.Value && job.CreatedAt == at),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_AcceptedRequest_DoesNotReadOrOverwriteBytes()
    {
        var request = new UploadJobOptions { RequestId = Guid.NewGuid(), BatchId = Guid.NewGuid(), CreatedByUserId = Guid.NewGuid() };
        var accepted = UploadJob.Create("pack.zip", createdAt: DateTimeOffset.UtcNow, createdByUserId: request.CreatedByUserId, requestId: request.RequestId, batchId: request.BatchId);
        _jobs.GetByIdAsync(accepted.Id, Arg.Any<CancellationToken>()).Returns(accepted);
        using var stream = new ThrowingStream();
        var result = await AcceptAsync(stream, "pack.zip", request);
        result.Value.ShouldBe(accepted.Id);
        await _uploads.DidNotReceiveWithAnyArgs().AddStagedAsync(default!, default);
    }

    [Fact]
    public async Task Handle_AcceptedAfterFirstLookup_RechecksUnderLockBeforeCopying()
    {
        var request = new UploadJobOptions { RequestId = Guid.NewGuid(), BatchId = Guid.NewGuid() };
        var accepted = UploadJob.Create("pack.zip", createdAt: DateTimeOffset.UnixEpoch,
            requestId: request.RequestId, batchId: request.BatchId);
        _jobs.GetByIdAsync(accepted.Id, Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<Job?>(null), Task.FromResult<Job?>(accepted));
        string directory = Path.Combine(_directory, "temp", "jobs", accepted.Id.ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "extracted.rom"), "accepted worker bytes");
        using var source = new ThrowingStream();

        var result = await AcceptAsync(source, "pack.zip", request);

        result.Value.ShouldBe(accepted.Id);
        (await File.ReadAllTextAsync(Path.Combine(directory, "extracted.rom"))).ShouldBe("accepted worker bytes");
        await _uploads.DidNotReceiveWithAnyArgs().AddStagedAsync(default!, default);
    }

    [Fact]
    public async Task Handle_ChangedTrackedOnlyOption_CannotReuseRequest()
    {
        var request = new UploadJobOptions { RequestId = Guid.NewGuid(), BatchId = Guid.NewGuid(), TrackedOnly = true };
        var accepted = UploadJob.Create("pack.zip", createdAt: DateTimeOffset.UtcNow, requestId: request.RequestId, batchId: request.BatchId);
        _jobs.GetByIdAsync(accepted.Id, Arg.Any<CancellationToken>()).Returns(accepted);
        using var stream = new ThrowingStream();
        var result = await AcceptAsync(stream, "pack.zip", request);
        result.FirstError.ShouldBe(UploadErrors.RequestIdReused());
    }

    [Fact]
    public async Task Handle_AnotherOwner_CannotReuseRequest()
    {
        var request = new UploadJobOptions { RequestId = Guid.NewGuid(), BatchId = Guid.NewGuid(), CreatedByUserId = Guid.NewGuid() };
        var accepted = UploadJob.Create("pack.zip", createdAt: DateTimeOffset.UtcNow, createdByUserId: Guid.NewGuid(), requestId: request.RequestId, batchId: request.BatchId);
        _jobs.GetByIdAsync(accepted.Id, Arg.Any<CancellationToken>()).Returns(accepted);
        using var stream = new MemoryStream([1, 2, 3]);
        var result = await AcceptAsync(stream, "pack.zip", request);
        result.FirstError.ShouldBe(UploadErrors.RequestIdReused());
        await _uploads.DidNotReceiveWithAnyArgs().AddStagedAsync(default!, default);
    }

    [Fact]
    public async Task Handle_CopyFails_RemovesPartialWorkspaceAndDoesNotAcceptJob()
    {
        var id = Guid.NewGuid();
        using var stream = new ThrowingStream();
        await Should.ThrowAsync<IOException>(() => AcceptAsync(stream, "pack.zip", new UploadJobOptions { RequestId = id, BatchId = Guid.NewGuid() }));
        Directory.Exists(Path.Combine(_directory, "temp", "jobs", id.ToString("N"))).ShouldBeFalse();
        await _uploads.DidNotReceiveWithAnyArgs().AddStagedAsync(default!, default);
    }

    [Fact]
    public async Task Handle_UploadLargerThanFreeSpace_IsAConflictWithoutWorkspaceOrJob()
    {
        var id = Guid.NewGuid();
        using var stream = new OversizedStream();

        var result = await AcceptAsync(stream, "pack.zip", new UploadJobOptions { RequestId = id, BatchId = Guid.NewGuid() });

        result.FirstError.ShouldBe(UploadErrors.InsufficientStorage());
        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.Conflict);
        Directory.Exists(Path.Combine(_directory, "temp", "jobs", id.ToString("N"))).ShouldBeFalse();
        await _uploads.DidNotReceiveWithAnyArgs().AddStagedAsync(default!, default);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>A seekable upload that reports more bytes than any volume has free; it is never read.</summary>
    private sealed class OversizedStream : MemoryStream
    {
        public override long Length => long.MaxValue / 2;

        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("An upload rejected for free space is never copied.");
    }

    private sealed class ThrowingStream : MemoryStream
    {
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
            throw new IOException("Transfer interrupted");
    }
}
