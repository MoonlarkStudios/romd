using Romd.Admin.Application.Ingestion.Extraction;

namespace Romd.Infrastructure.Storage;

/// <summary>
///     Resolves archive extractors based on file extension.
/// </summary>
public sealed class ArchiveExtractorResolver : IArchiveExtractorResolver
{
    private readonly IReadOnlyList<IArchiveExtractor> _extractors;
    private readonly IReadOnlyList<string> _supportedExtensions;

    public ArchiveExtractorResolver(IEnumerable<IArchiveExtractor> extractors)
    {
        _extractors = extractors.ToList();
        _supportedExtensions = _extractors
            .SelectMany(e => e.SupportedExtensions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> SupportedExtensions => _supportedExtensions;

    public IArchiveExtractor? GetExtractor(string filename)
    {
        return _extractors.FirstOrDefault(e => e.CanHandle(filename));
    }

    public bool IsArchive(string filename)
    {
        return _extractors.Any(e => e.CanHandle(filename));
    }
}
