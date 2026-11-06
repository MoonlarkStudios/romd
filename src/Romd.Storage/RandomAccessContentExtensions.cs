using System.Buffers;

namespace Romd.Storage;

public static class RandomAccessContentExtensions
{
    /// <summary>Fills the destination or throws EndOfStreamException. Failure may leave a partial result.</summary>
    public static async ValueTask ReadExactlyAtAsync(this IRandomAccessContent content, long offset,
        Memory<byte> destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ct.ThrowIfCancellationRequested();
        // Validate the entire range before modifying the destination.
        if (offset > long.MaxValue - destination.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        while (!destination.IsEmpty)
        {
            int read = await content.ReadAtAsync(offset, destination, ct).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("The content ended before the requested range was read.");
            offset += read;
            destination = destination[read..];
        }
    }

    /// <summary>
    /// Creates a read-only seekable stream with its own position. By default disposal also disposes
    /// the reader. Use leaveOpen for shared readers. Stream operations are not thread-safe;
    /// the underlying reader's integrity guarantees apply, without whole-object verification.
    /// </summary>
    public static Stream AsStream(this IRandomAccessContent content, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new ContentStream(content, leaveOpen);
    }

    private sealed class ContentStream(IRandomAccessContent content, bool leaveOpen) : Stream
    {
        private long _position;
        private bool _disposed;
        public override bool CanRead => !_disposed;
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => false;
        public override long Length { get { CheckDisposed(); return content.Length; } }
        public override long Position
        {
            get { CheckDisposed(); return _position; }
            set { CheckDisposed(); ArgumentOutOfRangeException.ThrowIfNegative(value); _position = value; }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            CheckDisposed();
            long basis = origin switch
            {
                SeekOrigin.Begin => 0,
                SeekOrigin.Current => _position,
                SeekOrigin.End => content.Length,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            long position;
            try
            { position = checked(basis + offset); }
            catch (OverflowException ex) { throw new IOException("Seek exceeds the supported position range.", ex); }
            if (position < 0)
                throw new IOException("Cannot seek before the start of content.");
            return _position = position;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        }

        public override int Read(Span<byte> buffer)
        {
            CheckDisposed();
            if (buffer.IsEmpty)
                return 0;
            int requested = Math.Min(buffer.Length, 64 * 1024);
            byte[] rented = ArrayPool<byte>.Shared.Rent(requested);
            try
            {
                int count = Read(rented, 0, requested);
                rented.AsSpan(0, count).CopyTo(buffer);
                return count;
            }
            finally { ArrayPool<byte>.Shared.Return(rented); }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            ValidateBufferArguments(buffer, offset, count);
            return ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            CheckDisposed();
            int read = await content.ReadAtAsync(_position, buffer, ct).ConfigureAwait(false);
            _position += read;
            return read;
        }

        public override void Flush() => CheckDisposed();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        private void CheckDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                if (!leaveOpen)
                    content.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (!leaveOpen)
                await content.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}
