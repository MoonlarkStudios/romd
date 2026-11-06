using System.Diagnostics.CodeAnalysis;
using Microsoft.Win32.SafeHandles;

namespace Romd.Storage.FileSystem;

/// <summary>
/// Identifies one physical file. A deleted and republished object keeps its key but not its file, and may
/// have another layout, so a cached seek table is valid only for the generation it was read from.
/// </summary>
internal readonly record struct FileGeneration(long Length, DateTime LastWriteTimeUtc)
{
    internal static FileGeneration Of(SafeFileHandle handle) =>
        new(RandomAccess.GetLength(handle), File.GetLastWriteTimeUtc(handle));
}

/// <summary>
/// Seek tables shared by every positional reader of an object, evicted least recently used first to stay
/// within a byte budget. Tables are immutable once read. Concurrent first opens of one object may each read
/// its table; every copy is equivalent, and the last one added is kept.
/// </summary>
internal sealed class SeekIndexCache(long capacityBytes)
{
    private readonly object _gate = new();
    private readonly Dictionary<StorageKey, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _recency = [];
    private long _retainedBytes;

    internal int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    internal long RetainedBytes
    {
        get { lock (_gate) return _retainedBytes; }
    }

    internal bool TryGet(StorageKey key, FileGeneration generation,
        [NotNullWhen(true)] out SeekableZstandard.Index? index)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var node) && node.Value.Generation == generation)
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
                index = node.Value.Index;
                return true;
            }
        }

        index = null;
        return false;
    }

    internal void Add(StorageKey key, FileGeneration generation, SeekableZstandard.Index index)
    {
        long bytes = index.RetainedBytes;
        lock (_gate)
        {
            RemoveLocked(key);
            if (bytes > capacityBytes)
                return;
            _entries[key] = _recency.AddFirst(new Entry(key, generation, index, bytes));
            _retainedBytes += bytes;
            while (_retainedBytes > capacityBytes)
                RemoveLocked(_recency.Last!.Value.Key);
        }
    }

    internal void Remove(StorageKey key)
    {
        lock (_gate)
            RemoveLocked(key);
    }

    private void RemoveLocked(StorageKey key)
    {
        if (!_entries.Remove(key, out var node))
            return;
        _recency.Remove(node);
        _retainedBytes -= node.Value.Bytes;
    }

    private sealed record Entry(StorageKey Key, FileGeneration Generation, SeekableZstandard.Index Index, long Bytes);
}
