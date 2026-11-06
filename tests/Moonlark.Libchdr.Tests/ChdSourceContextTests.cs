using System.Runtime.CompilerServices;
using Moonlark.Libchdr.Internal;
using Xunit;

namespace Moonlark.Libchdr.Tests;

/// <summary>Exercises the generated callback table through unmanaged Cdecl function pointers.</summary>
[Collection(nameof(ChdSourceContextIsolation))]
public sealed unsafe class ChdSourceContextTests
{
    /// <summary>fread fills legal short source chunks and returns complete item counts.</summary>
    [Fact]
    public void ReadCallbackCombinesShortReads()
    {
        using var source = new Source(10, maximumRead: 2);
        using var context = new ChdSourceContext(source, leaveOpen: true);
        byte* buffer = stackalloc byte[6];
        Assert.Equal((nuint)3, context.Callbacks->fread(buffer, 2, 3, context.UserData));
        Assert.Equal(3, source.ReadCalls);
        for (int index = 0; index < 6; index++)
            Assert.Equal((byte)index, buffer[index]);
        Assert.Equal(10UL, context.Callbacks->fsize(context.UserData));
        context.ThrowIfFaulted();
    }

    /// <summary>Partial trailing items advance the position by every byte consumed.</summary>
    [Fact]
    public void ReadCallbackCountsOnlyCompleteItemsAtEof()
    {
        using var source = new Source(5, maximumRead: 2);
        using var context = new ChdSourceContext(source, leaveOpen: true);
        byte* buffer = stackalloc byte[9];
        Assert.Equal((nuint)1, context.Callbacks->fread(buffer, 3, 3, context.UserData));
        for (int index = 0; index < 5; index++)
            Assert.Equal((byte)index, buffer[index]);
        Assert.Equal((nuint)0, context.Callbacks->fread(buffer, 1, 1, context.UserData));
        Assert.Equal(0, context.Callbacks->fseek(context.UserData, -1, (int)SeekOrigin.Current));
        Assert.Equal((nuint)1, context.Callbacks->fread(buffer, 1, 1, context.UserData));
        Assert.Equal((byte)4, buffer[0]);
        context.ThrowIfFaulted();
    }

    /// <summary>No source is asked for bytes at or past its length, wherever a malformed CHD points: reads there return zero,
    /// and a read across the end asks only for the bytes before it.</summary>
    [Fact]
    public void SourcesAreNeverReadPastTheirLength()
    {
        using var source = new Source(10) { RejectPastLength = true };
        using var context = new ChdSourceContext(source, leaveOpen: true);
        byte[] managed = new byte[4];
        Assert.Equal(0, context.ReadSourceAt(10, managed));
        Assert.Equal(0, context.ReadSourceAt(1L << 40, managed));
        Assert.Equal(0, source.ReadCalls);
        Assert.Equal(2, context.ReadSourceAt(8, managed));
        Assert.Equal((byte)9, managed[1]);
        byte* native = stackalloc byte[4];
        Assert.Equal(0, context.Callbacks->fseek(context.UserData, 1L << 32, (int)SeekOrigin.Begin));
        Assert.Equal((nuint)0, context.Callbacks->fread(native, 1, 4, context.UserData));
        Assert.Equal(0, context.Callbacks->fseek(context.UserData, 7, (int)SeekOrigin.Begin));
        Assert.Equal((nuint)1, context.Callbacks->fread(native, 3, 2, context.UserData));
        context.ThrowIfFaulted();
    }

    /// <summary>The length is read from the source once and reused, so every read is bounded by the length libchdr was
    /// given and no read pays for another Length call.</summary>
    [Fact]
    public void SourceLengthIsReadOnce()
    {
        using var source = new Source(10);
        using var context = new ChdSourceContext(source, leaveOpen: true);
        byte* buffer = stackalloc byte[2];
        Assert.Equal(10UL, context.Callbacks->fsize(context.UserData));
        Assert.Equal((nuint)2, context.Callbacks->fread(buffer, 1, 2, context.UserData));
        Assert.Equal(2, context.ReadSourceAt(4, new byte[2]));
        Assert.Equal(10, context.SourceLength);
        Assert.Equal(1, source.LengthCalls);
    }

    /// <summary>Native positions belong to each context rather than a shared data source.</summary>
    [Fact]
    public void ContextsMaintainIndependentSeekPositions()
    {
        using var source = new Source(10);
        using var first = new ChdSourceContext(source, leaveOpen: true);
        using var second = new ChdSourceContext(source, leaveOpen: true);
        byte* buffer = stackalloc byte[2];
        Assert.Equal(0, first.Callbacks->fseek(first.UserData, 5, (int)SeekOrigin.Begin));
        Assert.Equal((nuint)2, first.Callbacks->fread(buffer, 1, 2, first.UserData));
        Assert.Equal((byte)5, buffer[0]);
        Assert.Equal((nuint)2, second.Callbacks->fread(buffer, 1, 2, second.UserData));
        Assert.Equal((byte)0, buffer[0]);
        Assert.Equal(0, first.Callbacks->fseek(first.UserData, -1, (int)SeekOrigin.End));
        Assert.Equal((nuint)1, first.Callbacks->fread(buffer, 1, 2, first.UserData));
        Assert.Equal((byte)9, buffer[0]);
    }

    /// <summary>Positional metadata reads never mutate the native callback cursor.</summary>
    [Fact]
    public void ManagedPositionalReadsPreserveNativePosition()
    {
        using var source = new Source(10);
        using var context = new ChdSourceContext(source, leaveOpen: true);
        Assert.Equal(0, context.Callbacks->fseek(context.UserData, 2, (int)SeekOrigin.Begin));
        Span<byte> bytes = stackalloc byte[1];
        Assert.Equal(1, context.ReadSourceAt(8, bytes));
        Assert.Equal((byte)8, bytes[0]);
        Assert.Equal(10, context.SourceLength);
        fixed (byte* buffer = bytes)
            Assert.Equal((nuint)1, context.Callbacks->fread(buffer, 1, 1, context.UserData));
        Assert.Equal((byte)2, bytes[0]);
    }

    /// <summary>Malformed native requests fail before any pointer dereference or source access.</summary>
    [Fact]
    public void ReadCallbackRejectsInvalidArithmeticWithoutManagedFault()
    {
        using var source = new Source(10);
        using var context = new ChdSourceContext(source, leaveOpen: true);
        byte* buffer = stackalloc byte[1];
        Assert.Equal((nuint)0, context.Callbacks->fread(null, 0, 3, context.UserData));
        Assert.Equal((nuint)0, context.Callbacks->fread(null, 3, 0, context.UserData));
        Assert.Equal((nuint)0, context.Callbacks->fread(buffer, nuint.MaxValue, 2, context.UserData));
        Assert.Equal((nuint)0, context.Callbacks->fread(buffer, (nuint)int.MaxValue + 1, 1, context.UserData));
        Assert.Equal((nuint)0, context.Callbacks->fread((void*)nuint.MaxValue, 1, 1, context.UserData));
        Assert.Equal((nuint)0, context.Callbacks->fread(null, 1, 1, context.UserData));
        Assert.Equal(0, context.Callbacks->fseek(context.UserData, long.MaxValue, (int)SeekOrigin.Begin));
        Assert.Equal((nuint)0, context.Callbacks->fread(buffer, 1, 1, context.UserData));
        Assert.Equal(0, source.ReadCalls);
        context.ThrowIfFaulted();
    }

    /// <summary>Null userdata cannot leak a managed exception through any callback.</summary>
    [Fact]
    public void CallbacksRejectNullUserData()
    {
        using var source = new Source(10);
        using var context = new ChdSourceContext(source, leaveOpen: true);
        byte* buffer = stackalloc byte[1];
        Assert.Equal(ulong.MaxValue, context.Callbacks->fsize(null));
        Assert.Equal((nuint)0, context.Callbacks->fread(buffer, 1, 1, null));
        Assert.Equal(-1, context.Callbacks->fseek(null, 0, (int)SeekOrigin.Begin));
        Assert.Equal(-1, context.Callbacks->fclose(null));
        Assert.Equal(0, source.ReadCalls);
        Assert.Equal(0, source.CloseCount);
        context.ThrowIfFaulted();
    }

    /// <summary>Rejected origins and checked seek targets leave the last good position intact.</summary>
    [Fact]
    public void SeekCallbackPreservesPositionOnFailure()
    {
        using var source = new Source(10);
        using var context = new ChdSourceContext(source, leaveOpen: true);
        byte* buffer = stackalloc byte[1];
        Assert.Equal(0, context.Callbacks->fseek(context.UserData, 3, (int)SeekOrigin.Begin));
        Assert.Equal(-1, context.Callbacks->fseek(context.UserData, -4, (int)SeekOrigin.Current));
        Assert.Equal(-1, context.Callbacks->fseek(context.UserData, long.MaxValue, (int)SeekOrigin.Current));
        Assert.Equal(-1, context.Callbacks->fseek(context.UserData, 0, -1));
        Assert.Equal(-1, context.Callbacks->fseek(context.UserData, 0, 3));
        Assert.Equal((nuint)1, context.Callbacks->fread(buffer, 1, 1, context.UserData));
        Assert.Equal((byte)3, buffer[0]);
        context.ThrowIfFaulted();
    }

    /// <summary>Source faults cross back only after native return, with the original identity and stack.</summary>
    [Fact]
    public void ReadCallbackRetainsCompletedItemsAndRestoresSourceFault()
    {
        using var source = new Source(10, maximumRead: 2) { FailReadCall = 2 };
        using var context = new ChdSourceContext(source, leaveOpen: true);
        byte* buffer = stackalloc byte[6];
        Assert.Equal((nuint)1, context.Callbacks->fread(buffer, 2, 3, context.UserData));
        Assert.Equal((nuint)0, context.Callbacks->fread(buffer, 1, 1, context.UserData));
        Assert.Equal(2, source.ReadCalls);
        var fault = Assert.Throws<IOException>(context.ThrowIfFaulted);
        Assert.Same(source.ReadFault, fault);
        Assert.Contains("Source.Read", fault.StackTrace, StringComparison.Ordinal);
        context.ThrowIfFaulted();
        Assert.Equal((nuint)1, context.Callbacks->fread(buffer, 2, 1, context.UserData));
        Assert.Equal((byte)2, buffer[0]);
    }

    /// <summary>Length and end-seek source exceptions are restored rather than escaping the thunk.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceLengthFaultIsCaptured(bool seek)
    {
        using var source = new Source(10) { FailLength = true };
        using var context = new ChdSourceContext(source, leaveOpen: true);
        if (seek)
            Assert.Equal(-1, context.Callbacks->fseek(context.UserData, 0, (int)SeekOrigin.End));
        else
            Assert.Equal(ulong.MaxValue, context.Callbacks->fsize(context.UserData));
        Assert.Same(source.LengthFault, Assert.Throws<IOException>(context.ThrowIfFaulted));
        source.FailLength = false;
        Assert.Equal(10UL, context.Callbacks->fsize(context.UserData));
    }

    /// <summary>Negative source lengths are source contract faults rather than unsigned native sizes.</summary>
    [Fact]
    public void NegativeSourceLengthIsCaptured()
    {
        using var source = new Source(-1);
        using var context = new ChdSourceContext(source, leaveOpen: true);
        Assert.Equal(ulong.MaxValue, context.Callbacks->fsize(context.UserData));
        Assert.Throws<IOException>(context.ThrowIfFaulted);
    }

    /// <summary>Impossible source byte counts are contract faults, never position updates.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void SourceByteCountViolationIsCaptured(int read)
    {
        using var source = new Source(10) { InvalidRead = read };
        using var context = new ChdSourceContext(source, leaveOpen: true);
        byte* buffer = stackalloc byte[2];
        Assert.Equal((nuint)0, context.Callbacks->fread(buffer, 1, 2, context.UserData));
        Assert.Throws<IOException>(context.ThrowIfFaulted);
        source.InvalidRead = null;
        Assert.Equal((nuint)1, context.Callbacks->fread(buffer, 1, 1, context.UserData));
        Assert.Equal((byte)0, buffer[0]);
    }

    /// <summary>Native close consumes owned sources once and preserves sources opened with leaveOpen.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeCloseAndManagedDisposeHonorOwnership(bool leaveOpen)
    {
        var source = new Source(10);
        using var context = new ChdSourceContext(source, leaveOpen);
        var callbacks = context.Callbacks;
        void* userData = context.UserData;
        Assert.Equal(0, callbacks->fclose(userData));
        Assert.Equal(0, callbacks->fclose(userData));
        context.Dispose();
        context.Dispose();
        Assert.Equal(leaveOpen ? 0 : 1, source.CloseCount);
        context.ThrowIfFaulted();
        Assert.Throws<ObjectDisposedException>(() => context.SourceLength);
        Assert.Throws<ObjectDisposedException>(() => context.ReadSourceAt(0, Array.Empty<byte>()));
    }

    /// <summary>Disposal remains nonthrowing while a fresh close exception is available to managed callers.</summary>
    [Fact]
    public void ConsumedReadFaultDoesNotMaskLaterCloseFault()
    {
        var source = new Source(10) { FailReadCall = 1, FailClose = true };
        using var context = new ChdSourceContext(source, leaveOpen: false);
        byte* buffer = stackalloc byte[1];
        Assert.Equal((nuint)0, context.Callbacks->fread(buffer, 1, 1, context.UserData));
        Assert.Same(source.ReadFault, Assert.Throws<IOException>(context.ThrowIfFaulted));
        Assert.Equal(-1, context.Callbacks->fclose(context.UserData));
        context.Dispose();
        Assert.Equal(1, source.CloseCount);
        Assert.Same(source.CloseFault, Assert.Throws<IOException>(context.ThrowIfFaulted));
        context.ThrowIfFaulted();
    }

    /// <summary>Managed cleanup also consumes the source when native ownership was never entered.</summary>
    [Fact]
    public void ManagedDisposeWithoutNativeCloseConsumesSourceOnce()
    {
        var source = new Source(10) { FailClose = true };
        using var context = new ChdSourceContext(source, leaveOpen: false);
        context.Dispose();
        context.Dispose();
        Assert.Equal(1, source.CloseCount);
        Assert.Same(source.CloseFault, Assert.Throws<IOException>(context.ThrowIfFaulted));
    }

    /// <summary>Warm callback reads and seeks perform no managed allocation.</summary>
    [Fact]
    public void CallbacksAllocateNothingAfterWarmup()
    {
        using var source = new Source(10);
        using var context = new ChdSourceContext(source, leaveOpen: true);
        var warmup = ExerciseCallbacks(context, 1_024);
        context.ThrowIfFaulted();
        Assert.Equal(0, warmup.Failures);

        var measured = ExerciseCallbacks(context, 100_000);
        context.ThrowIfFaulted();
        Assert.Equal(0, measured.Failures);
        Assert.Equal(0, measured.Allocated);
    }

    /// <summary>The measurement must detect a source that allocates on every successful callback read.</summary>
    [Fact]
    public void CallbackMeasurementDetectsRecurringSourceAllocations()
    {
        using var source = new AllocatingSource();
        using var context = new ChdSourceContext(source, leaveOpen: true);
        var warmup = ExerciseCallbacks(context, 1_024);
        context.ThrowIfFaulted();
        Assert.Equal(0, warmup.Failures);

        var measured = ExerciseCallbacks(context, 100_000);
        context.ThrowIfFaulted();
        Assert.Equal(0, measured.Failures);
        Assert.True(measured.Allocated >= 100_000L * IntPtr.Size);
    }

    // Warm and measure the same call sites. One final pass must allocate exactly zero bytes;
    // no window is discarded and no tolerance or runtime setting changes the requirement.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long Allocated, int Failures) ExerciseCallbacks(ChdSourceContext context, int calls)
    {
        var callbacks = context.Callbacks;
        void* userData = context.UserData;
        byte* buffer = stackalloc byte[2];
        int failures = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < calls; index++)
        {
            buffer[0] = byte.MaxValue;
            buffer[1] = byte.MaxValue;
            int seek = callbacks->fseek(userData, 0, (int)SeekOrigin.Begin);
            nuint read = callbacks->fread(buffer, 1, 2, userData);
            ulong size = callbacks->fsize(userData);
            if (seek != 0)
                failures++;
            if (read != 2)
                failures++;
            if (size != 10)
                failures++;
            if ((buffer[0] != 0) | (buffer[1] != 1))
                failures++;
        }
        return (GC.GetAllocatedBytesForCurrentThread() - before, failures);
    }

    private sealed class AllocatingSource : IChdDataSource
    {
        private readonly Source _source = new(10);
        public long Length => _source.Length;
        public int Read(long offset, Span<byte> destination)
        {
            GC.KeepAlive(new object());
            return _source.Read(offset, destination);
        }
        public void Dispose() => _source.Dispose();
    }

    private sealed class Source(long length, int maximumRead = int.MaxValue) : IChdDataSource
    {
        internal int ReadCalls { get; private set; }
        internal int CloseCount { get; private set; }
        internal int? InvalidRead { get; set; }
        internal int? FailReadCall { get; set; }
        internal bool FailLength { get; set; }
        internal bool FailClose { get; set; }
        internal bool RejectPastLength { get; set; }
        internal int LengthCalls { get; private set; }
        internal IOException ReadFault { get; } = new("Read failed.");
        internal IOException LengthFault { get; } = new("Length failed.");
        internal IOException CloseFault { get; } = new("Close failed.");
        public long Length
        {
            get
            {
                LengthCalls++;
                return FailLength ? throw LengthFault : length;
            }
        }
        public int Read(long offset, Span<byte> destination)
        {
            ReadCalls++;
            if (RejectPastLength && offset + destination.Length > length)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (ReadCalls == FailReadCall)
                throw ReadFault;
            if (InvalidRead is { } invalid)
                return invalid;
            int read = (int)Math.Min(Math.Min(destination.Length, maximumRead), Math.Max(0, length - offset));
            for (int index = 0; index < read; index++)
                destination[index] = (byte)(offset + index);
            return read;
        }
        public void Dispose()
        {
            CloseCount++;
            if (FailClose)
                throw CloseFault;
        }
    }
}
