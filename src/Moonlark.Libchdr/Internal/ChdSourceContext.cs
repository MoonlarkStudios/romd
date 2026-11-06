using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Moonlark.Libchdr.Interop;

namespace Moonlark.Libchdr.Internal;

/// <summary>Owns stable callback storage and one native handle's seek position.</summary>
/// <remarks>The GC root holds this context, never its owning SafeHandle. Release callback storage only after native calls return.</remarks>
internal sealed unsafe class ChdSourceContext : IDisposable
{
    private readonly IChdDataSource _source;
    private readonly bool _leaveOpen;
    private core_file_callbacks* _callbacks;
    private GCHandle _root;
    private long _position;
    private long _length = -1;
    private int _closed;
    private int _disposed;
    private Exception? _fault;
    private ExceptionDispatchInfo? _dispatch;

    internal ChdSourceContext(IChdDataSource source, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _leaveOpen = leaveOpen;
        _callbacks = (core_file_callbacks*)NativeMemory.Alloc((nuint)sizeof(core_file_callbacks));
        if (_callbacks == null)
            throw new OutOfMemoryException();
        try
        {
            _root = GCHandle.Alloc(this);
            *_callbacks = new() { fsize = &Size, fread = &Read, fclose = &Close, fseek = &Seek };
        }
        catch
        {
            NativeMemory.Free(_callbacks);
            _callbacks = null;
            if (_root.IsAllocated)
                _root.Free();
            throw;
        }
    }

    internal core_file_callbacks* Callbacks
    {
        get { ThrowIfUnavailable(); return _callbacks; }
    }
    internal void* UserData
    {
        get { ThrowIfUnavailable(); return (void*)GCHandle.ToIntPtr(_root); }
    }
    /// <summary>The source's length, read once and reused: a source must not change while open, so every read is
    /// bounded by the length libchdr was given.</summary>
    internal long SourceLength
    {
        get
        {
            ThrowIfUnavailable();
            if (_length >= 0)
                return _length;
            long length = _source.Length;
            if (length < 0)
                throw new IOException("The CHD data source returned a negative length.");
            _length = length;
            return length;
        }
    }
    /// <summary>Reads at an absolute offset, never asking the source for bytes at or past its length: a malformed header or
    /// map can name an offset past the end, and some sources cannot even be positioned there.</summary>
    internal int ReadSourceAt(long offset, Span<byte> destination)
    {
        ThrowIfUnavailable();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        _ = checked(offset + destination.Length);
        long available = SourceLength - offset;
        if (available <= 0)
            return 0;
        if (available < destination.Length)
            destination = destination[..(int)available];
        int read = _source.Read(offset, destination);
        if ((uint)read > (uint)destination.Length)
            throw new IOException("The CHD data source returned an invalid byte count.");
        return read;
    }

    internal void ThrowIfFaulted()
    {
        Exception? fault = Interlocked.Exchange(ref _fault, null);
        ExceptionDispatchInfo? dispatch = Interlocked.Exchange(ref _dispatch, null);
        if (fault is null)
            return;
        if (dispatch?.SourceException == fault)
            dispatch.Throw();
        ExceptionDispatchInfo.Throw(fault);
    }

    internal void CaptureFault(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _fault, exception, null) is not null)
            return;
        try
        { _dispatch = ExceptionDispatchInfo.Capture(exception); }
        catch (Exception)
        {
            // Even allocation failure cannot escape a native callback; retain the original exception for managed rethrow.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        CloseSource();
        NativeMemory.Free(_callbacks);
        _callbacks = null;
        if (_root.IsAllocated)
            _root.Free();
    }

    private void ThrowIfUnavailable() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _closed) != 0, this);
    private bool CanRead => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _closed) == 0 && Volatile.Read(ref _fault) is null;
    private int CloseSource()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0 || _leaveOpen)
            return 0;
        try
        { _source.Dispose(); return 0; }
        catch (Exception exception) { CaptureFault(exception); return -1; }
    }
    private nuint ReadItems(void* buffer, nuint size, nuint count)
    {
        if (!CanRead || size == 0 || count == 0 || size > nuint.MaxValue / count)
            return 0;
        nuint bytes = size * count;
        if (bytes > int.MaxValue || buffer == null || (nuint)buffer > nuint.MaxValue - bytes ||
            _position > long.MaxValue - (long)bytes)
            return 0;
        nuint consumed = 0;
        try
        {
            Span<byte> destination = new(buffer, (int)bytes);
            while (consumed < bytes)
            {
                int read = ReadSourceAt(_position, destination[(int)consumed..]);
                if (read == 0)
                    break;
                _position += read;
                consumed += (nuint)read;
            }
        }
        catch (Exception exception) { CaptureFault(exception); }
        return consumed / size;
    }
    private int SeekPosition(long offset, int origin)
    {
        if (!CanRead || origin is < 0 or > 2)
            return -1;
        long basis = (SeekOrigin)origin switch
        { SeekOrigin.Begin => 0, SeekOrigin.Current => _position, _ => SourceLength };
        long target;
        try
        { target = checked(basis + offset); }
        catch (OverflowException) { return -1; }
        if (target < 0)
            return -1;
        _position = target;
        return 0;
    }
    private static ChdSourceContext Resolve(void* userData) =>
        GCHandle.FromIntPtr((nint)userData).Target as ChdSourceContext
        ?? throw new InvalidOperationException("The CHD callback context is unavailable.");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong Size(void* userData)
    {
        ChdSourceContext? context = null;
        try
        {
            context = Resolve(userData);
            return context.CanRead ? (ulong)context.SourceLength : ulong.MaxValue;
        }
        catch (Exception exception) { context?.CaptureFault(exception); return ulong.MaxValue; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nuint Read(void* buffer, nuint size, nuint count, void* userData)
    {
        ChdSourceContext? context = null;
        try
        { context = Resolve(userData); return context.ReadItems(buffer, size, count); }
        catch (Exception exception) { context?.CaptureFault(exception); return 0; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Close(void* userData)
    {
        ChdSourceContext? context = null;
        try
        { context = Resolve(userData); return context.CloseSource(); }
        catch (Exception exception) { context?.CaptureFault(exception); return -1; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Seek(void* userData, long offset, int origin)
    {
        ChdSourceContext? context = null;
        try
        { context = Resolve(userData); return context.SeekPosition(offset, origin); }
        catch (Exception exception) { context?.CaptureFault(exception); return -1; }
    }
}
