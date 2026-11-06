using System.Buffers;
using Moonlark.Libchdr.Internal;
using Moonlark.Libchdr.Interop;

namespace Moonlark.Libchdr.Benchmarks;

/// <summary>Benchmark-only direct generated native calls with the production callback source and an explicit one-hunk cache.</summary>
internal sealed unsafe class RawChd : IDisposable
{
    private readonly ChdSourceContext _context;
    private readonly byte[] _cache;
    private chd_file* _file;
    private uint _cachedHunk = uint.MaxValue;
    internal int HunkBytes { get; }
    internal ulong LogicalBytes { get; }

    internal RawChd(string path)
    {
        var source = new FileChdDataSource(path);
        try
        { _context = new ChdSourceContext(source, leaveOpen: false); }
        catch { source.Dispose(); throw; }
        try
        {
            LibchdrLibrary.EnsureInitialized();
            chd_file* file = null;
            chd_error error = NativeMethods.chd_open_core_file_callbacks(_context.Callbacks, _context.UserData, 1, null, &file);
            _file = file;
            Check(error);
            chd_header* header = NativeMethods.chd_get_header(_file);
            HunkBytes = checked((int)header->hunkbytes);
            LogicalBytes = header->logicalbytes;
            Check(NativeMethods.chd_set_cache_budget(_file, 0));
            if (NativeMethods.chd_get_cache_budget(_file) != 0)
                throw new InvalidDataException("Raw read-ahead budget is not zero.");
            _cache = ArrayPool<byte>.Shared.Rent(HunkBytes);
        }
        catch
        {
            if (_file != null)
                NativeMethods.chd_close(_file);
            _context.Dispose();
            throw;
        }
    }

    internal void ReadHunk(uint hunk, Span<byte> destination)
    {
        fixed (byte* buffer = destination)
            Check(NativeMethods.chd_read(_file, hunk, buffer));
    }

    // Explicitly an adapter baseline, not an invented claim that libchdr exposes ReadAt.
    internal int ReadAt(long offset, Span<byte> destination)
    {
        int count = (int)Math.Min((ulong)destination.Length, LogicalBytes - (ulong)offset);
        int written = 0;
        while (written < count)
        {
            ulong position = (ulong)offset + (uint)written;
            uint hunk = (uint)(position / (uint)HunkBytes);
            int within = (int)(position % (uint)HunkBytes);
            if (_cachedHunk != hunk)
            {
                _cachedHunk = uint.MaxValue;
                ReadHunk(hunk, _cache);
                _cachedHunk = hunk;
            }
            int take = Math.Min(count - written, HunkBytes - within);
            _cache.AsSpan(within, take).CopyTo(destination[written..]);
            written += take;
        }
        return count;
    }

    private void Check(chd_error error)
    {
        _context.ThrowIfFaulted();
        if (error != chd_error.CHDERR_NONE)
            throw new InvalidDataException("Raw native benchmark call failed: " + error);
    }

    public void Dispose()
    {
        if (_file == null)
            return;
        NativeMethods.chd_close(_file);
        _file = null;
        _context.Dispose();
        ArrayPool<byte>.Shared.Return(_cache);
    }
}
