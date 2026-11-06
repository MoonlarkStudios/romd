namespace Romd.Admin.Application.Ingestion.Classification;

/// <summary>
///     Classifies files based on content (magic bytes, structure).
/// </summary>
public interface IFileClassifier
{
    /// <summary>
    ///     Classifies a file based on its content.
    ///     Stream position is reset after classification.
    /// </summary>
    Task<FileClassification> ClassifyAsync(
        Stream content,
        string filename,
        CancellationToken cancellationToken = default);
}
