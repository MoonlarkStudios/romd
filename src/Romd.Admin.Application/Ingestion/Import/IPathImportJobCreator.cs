using ErrorOr;
using Romd.Admin.Application.Ingestion.Jobs;

namespace Romd.Admin.Application.Ingestion.Import;

/// <summary>
///     Creates upload jobs from a server-local directory. Population always copies into the job
///     workspace; when <c>move</c> is requested the source originals are deleted only after the
///     job completes fully successfully.
/// </summary>
public interface IPathImportJobCreator
{
    Task<ErrorOr<UploadJobCreationResult>> CreateFromPathAsync(
        string sourcePath,
        UploadJobOptions options,
        bool move,
        CancellationToken ct = default);
}
