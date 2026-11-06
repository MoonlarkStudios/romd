using System.Security.Cryptography;
using NSubstitute;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Storage;

namespace Romd.Infrastructure.Tests.Helpers;

internal static class FileLedgerSubstitutes
{
    /// <summary>Records every published file as a new row, numbered from <paramref name="firstId" /> in batch order.</summary>
    internal static IFileStorageService PublishesEveryFile(this IFileStorageService files, int firstId = 10)
    {
        files.PublishStagedAsync(Arg.Any<IReadOnlyList<IStagedFile>>(), Arg.Any<CancellationToken>()).Returns(call =>
            new PublishedFiles(call.Arg<IReadOnlyList<IStagedFile>>().Select((_, index) => new FileStoreResult(
                FileEntity.Rehydrate(firstId + index, Sha256.FromSpan(SHA256.HashData([(byte)index])), 3, 3, false,
                    DateTimeOffset.UtcNow), true, false)).ToArray()));
        return files;
    }

    internal static void ShouldNotHavePublished(this IFileStorageService files)
    {
        files.DidNotReceiveWithAnyArgs().PublishStagedAsync((IReadOnlyList<IStagedFile>)null!, default);
    }

    private sealed class PublishedFiles(IReadOnlyList<FileStoreResult> files) : IStagedFilePublicationBatch
    {
        public bool RequiresFlush => false;
        public IReadOnlyList<FileStoreResult> ReadAfterFlush() => files;
    }
}
