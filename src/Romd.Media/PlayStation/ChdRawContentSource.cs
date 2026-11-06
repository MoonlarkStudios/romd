using Moonlark.Libchdr;
using Romd.Storage;

namespace Romd.Media.PlayStation;

/// <summary>Owns synchronous positional access to an immutable raw CAS object.</summary>
public sealed class ChdRawContentSource : IChdDataSource
{
    private readonly ISynchronousRawContent _content;

    public ChdRawContentSource(ISynchronousRawContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _content = content;
    }

    public long Length => _content.Length;

    public int Read(long offset, Span<byte> destination) => _content.ReadAt(offset, destination);

    public void Dispose() => _content.Dispose();
}
