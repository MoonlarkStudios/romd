namespace Romd.Admin.Application.Storage.Files;

/// <summary>
///     Factory for creating local temporary files for processing.
/// </summary>
public interface ITempFileFactory
{
    /// <summary>
    ///     Creates a temporary file from the provided stream.
    /// </summary>
    Task<ITempFile> CreateAsync(Stream source, CancellationToken cancellationToken = default);
}
