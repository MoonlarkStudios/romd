using System.Diagnostics.CodeAnalysis;
using Moonlark.Libchdr.Internal;
using Moonlark.Libchdr.Interop;

namespace Moonlark.Libchdr;

/// <summary>A synchronous, read-only CHD decoder with owned native lifetime.</summary>
/// <remarks>Instances and their streams/views are not thread-safe. Use independent handles
/// for concurrent decodes: a read that overlaps another read of the same instance throws
/// <see cref="InvalidOperationException"/> rather than entering native code twice. Untrusted containers require a
/// separate resource-limited process.
/// Opening validates the codecs and the whole hunk map, so the source's bytes must not change while the file is open.</remarks>
public sealed unsafe class ChdFile : IDisposable
{
    private readonly ChdSafeHandle _handle;
    private readonly ChdHeader _header;
    private readonly MetadataEntry[] _metadata;
    private readonly ulong _readAheadBytes;
    private uint _cachedHunk = uint.MaxValue;
    private int _disposed;
    private int _reading;

    private ChdFile(ChdSafeHandle handle, ChdHeader header, MetadataEntry[] metadata, ulong budget)
    { _handle = handle; _header = header; _metadata = metadata; _readAheadBytes = budget; }

    /// <summary>The immutable header snapshot.</summary>
    public ChdHeader Header { get { ThrowIfDisposed(); return _header; } }

    /// <summary>The compressed read-ahead budget actually accepted, never exceeding the requested ceiling.</summary>
    public ulong ReadAheadBytes { get { ThrowIfDisposed(); return _readAheadBytes; } }

    /// <summary>Opens a file with positional I/O and closes it with the decoder.</summary>
    /// <param name="path">A CHD file path.</param>
    /// <param name="options">Read-ahead options, or defaults.</param>
    /// <returns>The opened decoder.</returns>
    public static ChdFile Open(string path, ChdOpenOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        IChdDataSource source;
        try
        { source = new FileChdDataSource(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { throw new ChdException(ChdError.FileNotFound, $"open '{path}'"); }
        return Open(source, false, options);
    }

    /// <summary>Opens a positional source, with a cursor private to this native handle.</summary>
    /// <param name="source">The source; it must return valid byte counts and a nonnegative length.</param>
    /// <param name="leaveOpen">Whether to preserve the source on successful or failed open and disposal.</param>
    /// <param name="options">Read-ahead options, or defaults.</param>
    /// <returns>The opened decoder.</returns>
    /// <remarks>Source exceptions retain their original identity and managed stack.</remarks>
    public static ChdFile Open(IChdDataSource source, bool leaveOpen, ChdOpenOptions? options)
    {
        if (TryOpenCore(source, leaveOpen, options, out ChdFile? file, out ChdError error))
            return file;
        throw new ChdException(error, "open source");
    }

    private static bool TryOpenCore(IChdDataSource source, bool leaveOpen, ChdOpenOptions? options,
        [NotNullWhen(true)] out ChdFile? opened, out ChdError failure)
    {
        ArgumentNullException.ThrowIfNull(source);
        opened = null;
        failure = ChdError.None;
        ChdSourceContext? context = null;
        ChdSafeHandle? handle = null;
        chd_file* file = null;
        try
        {
            context = new(source, leaveOpen);
            LibchdrLibrary.EnsureInitialized();
            MetadataEntry[] metadata = ChdHeaderReader.ValidateEnvelope(context);
            ChdError error = (ChdError)NativeMethods.chd_open_core_file_callbacks(context.Callbacks, context.UserData, 1, null, &file);
            context.ThrowIfFaulted();
            if (error != ChdError.None)
                throw new ChdValidationException(error, "open source");
            handle = new(file, context);
            chd_header* native = NativeMethods.chd_get_header(file);
            ChdHeader header = ChdHeaderReader.Snapshot(native);
            ChdMapValidator.Validate(native, context);
            context.ThrowIfFaulted();
            // libchdr allocates the whole ceiling and its window holds file bytes, so nothing beyond the source helps.
            ulong requested = options?.ReadAheadBytes ?? 0;
            ulong ceiling = requested == 0 ? 0 : Math.Min(requested, (ulong)context.SourceLength);
            error = (ChdError)NativeMethods.chd_set_cache_budget(file, checked((nuint)ceiling));
            context.ThrowIfFaulted();
            // An allocation failure leaves read-ahead off and the file fully usable, as libchdr documents.
            if (error is not (ChdError.None or ChdError.OutOfMemory))
                throw new ChdValidationException(error, "set read-ahead budget");
            ulong actual = NativeMethods.chd_get_cache_budget(file);
            if (actual > ceiling)
                throw new ChdValidationException(ChdError.InvalidState, "read-ahead ceiling");
            opened = new(handle, header, metadata, actual);
            return true;
        }
        catch (ChdValidationException error)
        {
            failure = error.Error;
            CloseFailedOpen(context, handle, file);
            return false;
        }
        catch (Exception error)
        {
            if (context is null)
            {
                if (!leaveOpen)
                    DisposeAfterSetupFailure(source);
                throw;
            }
            context.CaptureFault(error);
            CloseFailedOpen(context, handle, file);
            throw;
        }
    }

    /// <summary>Opens a readable seekable stream, preserving its visible position.</summary>
    /// <param name="seekableStream">The readable seekable stream.</param>
    /// <param name="leaveOpen">Whether disposal or failed open preserves the stream.</param>
    /// <param name="options">Read-ahead options, or defaults.</param>
    /// <returns>The opened decoder.</returns>
    public static ChdFile Open(Stream seekableStream, bool leaveOpen, ChdOpenOptions? options) =>
        Open(new StreamChdDataSource(seekableStream, leaveOpen), false, options);

    /// <summary>Attempts to open an expected possibly malformed file; programming/source errors still throw.</summary>
    /// <param name="path">The CHD path.</param>
    /// <param name="file">The opened decoder, or null.</param>
    /// <param name="error">The native/structural error, or None.</param>
    /// <param name="options">Read-ahead options, or defaults.</param>
    /// <returns>Whether open succeeded.</returns>
    public static bool TryOpen(string path, [NotNullWhen(true)] out ChdFile? file, out ChdError error, ChdOpenOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        IChdDataSource source;
        try
        { source = new FileChdDataSource(path); }
        catch (Exception failure) when (failure is FileNotFoundException or DirectoryNotFoundException)
        { file = null; error = ChdError.FileNotFound; return false; }
        return TryOpenCore(source, false, options, out file, out error);
    }

    /// <summary>Attempts to open a positional source; source exceptions still propagate unchanged.</summary>
    /// <param name="source">The positional source.</param>
    /// <param name="leaveOpen">Whether to preserve the source.</param>
    /// <param name="file">The opened decoder, or null.</param>
    /// <param name="error">The native/structural error, or None.</param>
    /// <param name="options">Read-ahead options, or defaults.</param>
    /// <returns>Whether open succeeded.</returns>
    public static bool TryOpen(IChdDataSource source, bool leaveOpen, [NotNullWhen(true)] out ChdFile? file, out ChdError error, ChdOpenOptions? options)
    {
        return TryOpenCore(source, leaveOpen, options, out file, out error);
    }

    /// <summary>Attempts to open a stream; stream exceptions still propagate unchanged.</summary>
    /// <param name="seekableStream">A readable seekable stream.</param>
    /// <param name="leaveOpen">Whether to preserve the stream.</param>
    /// <param name="file">The opened decoder, or null.</param>
    /// <param name="error">The native/structural error, or None.</param>
    /// <param name="options">Read-ahead options, or defaults.</param>
    /// <returns>Whether open succeeded.</returns>
    public static bool TryOpen(Stream seekableStream, bool leaveOpen, [NotNullWhen(true)] out ChdFile? file, out ChdError error, ChdOpenOptions? options)
    {
        return TryOpenCore(new StreamChdDataSource(seekableStream, leaveOpen), false, options, out file, out error);
    }

    /// <summary>Decodes a complete hunk directly into caller memory.</summary>
    /// <param name="index">The zero-based hunk index.</param>
    /// <param name="destination">At least HunkBytes of readable memory, aligned to two bytes.
    /// LZMA reads it back as a dictionary; do not expose it until decoding finishes.</param>
    /// <exception cref="InvalidOperationException">Another read of this instance is in progress.</exception>
    public void ReadHunk(uint index, Span<byte> destination)
    {
        ThrowIfDisposed();
        if (index >= _header.HunkCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (destination.Length < _header.HunkBytes)
            throw new ArgumentException("The destination must hold a complete CHD hunk.", nameof(destination));
        if (!TryEnterRead())
            throw Busy($"read hunk {index}");
        try
        { DecodeHunk(index, destination); }
        finally { ExitRead(); }
    }

    private void DecodeHunk(uint index, Span<byte> destination)
    {
        bool added = false;
        try
        {
            _handle.DangerousAddRef(ref added);
            fixed (byte* buffer = destination)
            {
                if (((nuint)buffer & 1) != 0)
                    throw new ArgumentException("CHD hunk memory must be at least two-byte aligned.", nameof(destination));
                ChdError error = (ChdError)NativeMethods.chd_read((chd_file*)_handle.DangerousGetHandle(), index, buffer);
                _handle.Context.ThrowIfFaulted();
                if (error != ChdError.None)
                    throw new ChdException(error, $"read hunk {index}");
            }
        }
        finally { if (added) _handle.DangerousRelease(); }
    }

    /// <summary>Reads logical bytes using an instance-owned single-hunk cache, returning zero at EOF.</summary>
    /// <param name="offset">A nonnegative logical byte offset.</param>
    /// <param name="destination">The caller-owned destination.</param>
    /// <returns>The number of logical bytes read, bounded by EOF.</returns>
    /// <exception cref="InvalidOperationException">Another read of this instance is in progress.</exception>
    public int ReadAt(long offset, Span<byte> destination)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if ((ulong)offset >= _header.LogicalBytes || destination.IsEmpty)
            return 0;
        int count = (int)Math.Min((ulong)destination.Length, _header.LogicalBytes - (ulong)offset);
        if (!TryEnterRead())
            throw Busy($"read at offset {offset}");
        bool added = false;
        try
        {
            // The reference keeps the handle-owned cache out of the pool across both the decode and the copy.
            _handle.DangerousAddRef(ref added);
            byte[] cache = _handle.HunkCache((int)_header.HunkBytes);
            int written = 0;
            while (written < count)
            {
                ulong position = checked((ulong)offset + (uint)written);
                uint hunk = (uint)(position / _header.HunkBytes);
                int within = (int)(position % _header.HunkBytes);
                if (_cachedHunk != hunk)
                {
                    ThrowIfDisposed();
                    _cachedHunk = uint.MaxValue;
                    DecodeHunk(hunk, cache);
                    _cachedHunk = hunk;
                }
                int take = Math.Min(count - written, (int)_header.HunkBytes - within);
                cache.AsSpan(within, take).CopyTo(destination[written..]);
                written += take;
            }
            return written;
        }
        finally
        {
            if (added)
                _handle.DangerousRelease();
            ExitRead();
        }
    }

    /// <summary>Claims the instance for one native-reading call; libchdr's per-file decode state cannot be shared.</summary>
    private bool TryEnterRead() => Interlocked.CompareExchange(ref _reading, 1, 0) == 0;

    private void ExitRead() => Volatile.Write(ref _reading, 0);

    private static InvalidOperationException Busy(string operation) =>
        new($"{operation}: another read of this ChdFile is in progress. An instance is not thread-safe and cannot be read from its own data source; use one instance per thread.");

    /// <summary>Enumerates validated metadata descriptors without allocating per entry.</summary>
    /// <returns>A struct enumerator in chain order.</returns>
    public ChdMetadataEnumerator EnumerateMetadata() { ThrowIfDisposed(); return new(this); }

    /// <summary>Copies a complete matching metadata payload; never silently truncates.</summary>
    /// <param name="tag">The exact tag, or Wildcard for all tags.</param>
    /// <param name="index">The zero-based index within matching entries.</param>
    /// <param name="destination">The caller buffer.</param>
    /// <param name="info">Entry information including required size, or default when absent.</param>
    /// <returns>False for an absent entry or short buffer; a short buffer is unchanged.</returns>
    /// <exception cref="InvalidOperationException">Another read of this instance is in progress.</exception>
    public bool TryGetMetadata(ChdMetadataTag tag, uint index, Span<byte> destination, out ChdMetadataInfo info)
    {
        ThrowIfDisposed();
        uint matched = 0;
        foreach (MetadataEntry entry in _metadata)
        {
            if (tag != ChdMetadataTag.Wildcard && entry.Info.Tag != tag)
                continue;
            if (matched++ != index)
                continue;
            info = entry.Info;
            if ((uint)destination.Length < info.Length)
                return false;
            ReadSourceExactly(entry.PayloadOffset, destination[..(int)info.Length]);
            return true;
        }
        info = default;
        return false;
    }

    /// <summary>Closes native and owned source resources; subsequent operations throw ObjectDisposedException.</summary>
    /// <remarks>A Dispose that races an in-flight read, which is unsupported, defers the native close until that read
    /// returns; a fault from closing the source is then not reported, and a source fault from that read may be reported
    /// by Dispose instead of by the read.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _handle.Dispose();
        _handle.Context.ThrowIfFaulted();
    }

    internal int MetadataCount { get { ThrowIfDisposed(); return _metadata.Length; } }
    internal ChdMetadataInfo MetadataAt(int index)
    {
        ThrowIfDisposed();
        if ((uint)index >= (uint)_metadata.Length)
            throw new InvalidOperationException("Metadata enumeration is outside a current entry.");
        return _metadata[index].Info;
    }
    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <inheritdoc cref="Open(string, ChdOpenOptions)"/>
    public static ChdFile Open(string path) => Open(path, null);

    /// <inheritdoc cref="Open(IChdDataSource, bool, ChdOpenOptions)"/>
    public static ChdFile Open(IChdDataSource source, bool leaveOpen) => Open(source, leaveOpen, null);

    /// <inheritdoc cref="Open(Stream, bool, ChdOpenOptions)"/>
    public static ChdFile Open(Stream seekableStream, bool leaveOpen) => Open(seekableStream, leaveOpen, null);

    /// <inheritdoc cref="TryOpen(string, out ChdFile, out ChdError, ChdOpenOptions)"/>
    public static bool TryOpen(string path, [NotNullWhen(true)] out ChdFile? file, out ChdError error) => TryOpen(path, out file, out error, null);

    /// <inheritdoc cref="TryOpen(IChdDataSource, bool, out ChdFile, out ChdError, ChdOpenOptions)"/>
    public static bool TryOpen(IChdDataSource source, bool leaveOpen, [NotNullWhen(true)] out ChdFile? file, out ChdError error) => TryOpen(source, leaveOpen, out file, out error, null);

    /// <inheritdoc cref="TryOpen(Stream, bool, out ChdFile, out ChdError, ChdOpenOptions)"/>
    public static bool TryOpen(Stream seekableStream, bool leaveOpen, [NotNullWhen(true)] out ChdFile? file, out ChdError error) => TryOpen(seekableStream, leaveOpen, out file, out error, null);

    internal int ReadMetadataPart(int index, uint offset, Span<byte> destination)
    {
        ThrowIfDisposed();
        MetadataEntry entry = _metadata[index];
        if (offset > entry.Info.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        int take = (int)Math.Min((uint)destination.Length, entry.Info.Length - offset);
        ReadSourceExactly(checked(entry.PayloadOffset + offset), destination[..take]);
        return take;
    }

    private void ReadSourceExactly(long offset, Span<byte> destination)
    {
        // Metadata reads never enter native code, but claiming them too means a data source never sees overlapping
        // reads from one instance.
        if (!TryEnterRead())
            throw Busy($"read metadata at offset {offset}");
        bool added = false;
        try
        {
            _handle.DangerousAddRef(ref added);
            try
            { ChdMetadataIndex.ReadExactly(_handle.Context, offset, destination); }
            catch (ChdValidationException error) { throw new ChdException(error.Error, error.Message); }
        }
        finally
        {
            if (added)
                _handle.DangerousRelease();
            ExitRead();
        }
    }

    private static void CloseFailedOpen(ChdSourceContext? context, ChdSafeHandle? handle, chd_file* file)
    {
        if (context is null)
            return;
        try
        {
            if (handle is not null)
                handle.Dispose();
            else if (file != null)
                NativeMethods.chd_close(file);
        }
        finally { context.Dispose(); }
        context.ThrowIfFaulted();
    }

    private static void DisposeAfterSetupFailure(IChdDataSource source)
    {
        try
        { source.Dispose(); }
        catch (Exception)
        {
            // Preserve the original construction fault if cleanup also fails.
        }
    }
}
