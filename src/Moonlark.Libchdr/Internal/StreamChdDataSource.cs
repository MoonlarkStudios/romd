using System.Runtime.CompilerServices;

namespace Moonlark.Libchdr.Internal;

/// <summary>Adapts a readable seekable stream without exposing or changing its cursor.</summary>
/// <remarks>Wrappers over the same stream share a gate. Concurrent access outside these wrappers is unsupported.</remarks>
internal sealed class StreamChdDataSource : IChdDataSource
{
    private static readonly ConditionalWeakTable<Stream, object> Gates = new();
    private readonly Stream _stream;
    private readonly object _gate;
    private readonly bool _leaveOpen;
    private bool _disposed;

    internal StreamChdDataSource(Stream stream, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _leaveOpen = leaveOpen;
        try
        {
            if (!stream.CanRead || !stream.CanSeek)
                throw new ArgumentException("CHD data requires a readable, seekable stream.", nameof(stream));
            _gate = Gates.GetValue(stream, static _ => new object());
        }
        catch
        {
            if (!leaveOpen)
            {
                try
                { stream.Dispose(); }
                catch (Exception) { /* Preserve the setup fault when owned-source closure also fails. */ }
            }
            throw;
        }
    }

    public long Length
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _stream.Length;
            }
        }
    }

    public int Read(long offset, Span<byte> destination)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            _ = checked(offset + destination.Length);
            long original = _stream.Position;
            Exception? fault = null;
            try
            {
                _stream.Position = offset;
                return _stream.Read(destination);
            }
            catch (Exception exception)
            {
                fault = exception;
                throw;
            }
            finally
            {
                if (fault is null)
                    _stream.Position = original;
                else
                    RestoreAfterFailure(original);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (!_leaveOpen)
                _stream.Dispose();
        }
    }

    private void RestoreAfterFailure(long position)
    {
        try
        { _stream.Position = position; }
        catch (Exception)
        {
            // The read/seek exception remains authoritative when restoration also fails.
        }
    }
}
