using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class MediaReconciliationPublicationIntegrationTests
{
    private static IFileStorageService WithStagingDisposalFailures(IFileStorageService inner, List<DisposalFaultStage> stages)
    {
        var files = Substitute.For<IFileStorageService>();
        async Task<IStagedFile> WrapAsync(Task<IStagedFile> pending)
        {
            var stage = new DisposalFaultStage(await pending);
            stages.Add(stage);
            return stage;
        }
        files.StageAsync(Arg.Any<ITempFile>(), Arg.Any<CancellationToken>()).Returns(call =>
            WrapAsync(inner.StageAsync(call.Arg<ITempFile>(), call.Arg<CancellationToken>())));
        files.StageRawAsync(Arg.Any<ITempFile>(), Arg.Any<CancellationToken>()).Returns(call =>
            WrapAsync(inner.StageRawAsync(call.Arg<ITempFile>(), call.Arg<CancellationToken>())));
        files.PublishStagedAsync(Arg.Any<IReadOnlyList<IStagedFile>>(), Arg.Any<CancellationToken>()).Returns(call =>
            inner.PublishStagedAsync(call.Arg<IReadOnlyList<IStagedFile>>().Select(stage => ((DisposalFaultStage)stage).Inner).ToArray(),
                call.Arg<CancellationToken>()));
        files.RecordArtifactChecksumsAsync(Arg.Any<int>(), Arg.Any<TempFileArtifactMeasurement>(), Arg.Any<CancellationToken>()).Returns(call =>
            inner.RecordArtifactChecksumsAsync(call.Arg<int>(), call.Arg<TempFileArtifactMeasurement>(), call.Arg<CancellationToken>()));
        return files;
    }

    private sealed class DisposalFaultStage(IStagedFile inner) : IStagedFile
    {
        public IStagedFile Inner => inner;
        public Sha256 Sha256 => inner.Sha256;
        public long Size => inner.Size;
        public Md5? ArtifactMd5 => inner.ArtifactMd5;
        public Sha1? ArtifactSha1 => inner.ArtifactSha1;
        public int Disposals { get; private set; }
        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            Disposals++;
            throw new IOException("Injected staging failure after real disposal");
        }
    }

    private sealed class ObservedPublicationFlush(IUnitOfWork inner) : IUnitOfWork
    {
        public Exception? Failure { get; private set; }
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public Task<ITransaction> BeginTransactionAsync(CancellationToken ct = default) => inner.BeginTransactionAsync(ct);
        public async Task FlushAsync(CancellationToken ct = default)
        {
            try
            { await inner.FlushAsync(ct); }
            catch (Exception error) { Failure = error; throw; }
        }
    }
}
