using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Configuration;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class UploadJobCreatorTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("romd-upload-acceptance-").FullName;
    private readonly IUploadJobRepository _uploads = Substitute.For<IUploadJobRepository>();
    private readonly IJobRepository _jobs = Substitute.For<IJobRepository>();

    private UploadJobCreator Create()
    {
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(_directory);
        return new UploadJobCreator(_uploads, _jobs, options, NullLogger<UploadJobCreator>.Instance);
    }

    [Fact]
    public async Task CreateAsync_AcceptedRequest_DoesNotReadOrOverwriteBytes()
    {
        var request = new UploadJobOptions { RequestId = Guid.NewGuid(), BatchId = Guid.NewGuid(), CreatedByUserId = Guid.NewGuid() };
        var accepted = UploadJob.Create("pack.zip", createdByUserId: request.CreatedByUserId, requestId: request.RequestId, batchId: request.BatchId);
        _jobs.GetByIdAsync(accepted.Id, Arg.Any<CancellationToken>()).Returns(accepted);
        using var stream = new ThrowingStream();
        var result = await Create().CreateAsync(stream, "pack.zip", request);
        result.JobId.ShouldBe(accepted.Id);
        await _uploads.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Fact]
    public async Task CreateAsync_AnotherOwner_CannotReuseRequest()
    {
        var request = new UploadJobOptions { RequestId = Guid.NewGuid(), BatchId = Guid.NewGuid(), CreatedByUserId = Guid.NewGuid() };
        var accepted = UploadJob.Create("pack.zip", createdByUserId: Guid.NewGuid(), requestId: request.RequestId, batchId: request.BatchId);
        _jobs.GetByIdAsync(accepted.Id, Arg.Any<CancellationToken>()).Returns(accepted);
        using var stream = new MemoryStream([1, 2, 3]);
        await Should.ThrowAsync<InvalidOperationException>(() => Create().CreateAsync(stream, "pack.zip", request));
    }

    [Fact]
    public async Task CreateAsync_CopyFails_RemovesPartialWorkspaceAndDoesNotAcceptJob()
    {
        var id = Guid.NewGuid();
        using var stream = new ThrowingStream();
        await Should.ThrowAsync<IOException>(() => Create().CreateAsync(stream, "pack.zip", new UploadJobOptions { RequestId = id, BatchId = Guid.NewGuid() }));
        Directory.Exists(Path.Combine(_directory, "temp", "jobs", id.ToString("N"))).ShouldBeFalse();
        await _uploads.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class ThrowingStream : MemoryStream
    {
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
            throw new IOException("Transfer interrupted");
    }
}
