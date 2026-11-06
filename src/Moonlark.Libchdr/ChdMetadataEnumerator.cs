namespace Moonlark.Libchdr;

/// <summary>Enumerates metadata descriptors without per-entry allocations.</summary>
public struct ChdMetadataEnumerator
{
    private readonly ChdFile? _file;
    private int _index;

    internal ChdMetadataEnumerator(ChdFile file) { _file = file; _index = -1; }

    /// <summary>The current entry; access outside a successful iteration throws.</summary>
    public readonly ChdMetadataInfo Current => File.MetadataAt(_index);

    /// <summary>Advances to the next entry in native chain order.</summary>
    /// <returns>Whether an entry is available.</returns>
    public bool MoveNext()
    {
        ChdFile file = File;
        file.ThrowIfDisposed();
        if (_index < file.MetadataCount)
            _index++;
        return _index < file.MetadataCount;
    }

    /// <summary>Starts a fresh enumeration over the same file.</summary>
    /// <returns>An allocation-free enumerator.</returns>
    public readonly ChdMetadataEnumerator GetEnumerator()
    {
        ChdFile file = File;
        file.ThrowIfDisposed();
        return new(file);
    }

    private readonly ChdFile File => _file ?? throw new InvalidOperationException("Metadata enumeration requires an opened CHD file.");
}
