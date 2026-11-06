using Romd.Domain.Hashing;
using Romd.Domain.Storage;

namespace Romd.Admin.Application.Storage.Files;

/// <summary>A publication's identity and flags, usable only in its original transaction after any required flush.</summary>
public interface IStagedFilePublication
{
    bool RequiresFlush { get; }
    FileStoreResult ReadAfterFlush();
}

/// <summary>Publications in input order, usable only in their original transaction, including an empty batch.</summary>
public interface IStagedFilePublicationBatch
{
    bool RequiresFlush { get; }
    IReadOnlyList<FileStoreResult> ReadAfterFlush();
}

/// <summary>Guards publication receipts against use after their original transaction ends or is replaced.</summary>
public interface IFilePublicationBoundary
{
    void EnsureActive();
}

/// <summary>
///     One insertion's identity inside its original transaction. The owner flushes and commits;
///     reading before flush or after leaving that boundary is rejected.
/// </summary>
public interface IStagedFileCreation
{
    bool RequiresFlush { get; }
    FileEntity ReadAfterFlush();
}

/// <summary>Identity-free facts used to validate another publication against pending creation.</summary>
public sealed record FilePublicationFacts(Sha256 Sha256, long Size, long SizeOnDisk, bool IsCompressed,
    DateTimeOffset CreatedAt, Md5? ArtifactMd5, Sha1? ArtifactSha1);

/// <summary>A canonical pending insertion and its facts; no database identity is exposed before flush.</summary>
public sealed record PendingFileCreation(FilePublicationFacts Facts, IStagedFileCreation Creation);
