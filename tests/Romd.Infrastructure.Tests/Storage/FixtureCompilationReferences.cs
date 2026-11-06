using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>Owns native metadata images only until a synthetic fixture has been emitted.</summary>
internal sealed class FixtureCompilationReferences : IDisposable
{
    private readonly List<AssemblyMetadata> _metadata = [];
    public ImmutableArray<MetadataReference> References { get; }

    public FixtureCompilationReferences(IEnumerable<string> paths)
    {
        var references = ImmutableArray.CreateBuilder<MetadataReference>();
        try
        {
            foreach (string path in paths.Distinct(StringComparer.Ordinal))
            {
                var metadata = AssemblyMetadata.CreateFromFile(path);
                _metadata.Add(metadata);
                references.Add(metadata.GetReference(filePath: path));
            }
            References = references.ToImmutable();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var metadata in _metadata)
            metadata.Dispose();
        _metadata.Clear();
    }
}
