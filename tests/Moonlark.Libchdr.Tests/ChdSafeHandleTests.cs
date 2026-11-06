using System.Runtime.CompilerServices;
using Moonlark.Libchdr.Internal;
using Moonlark.Libchdr.Interop;
using Xunit;

namespace Moonlark.Libchdr.Tests;

/// <summary>Checks adoption, native close, and finalization against verified synthetic fixtures.</summary>
public sealed unsafe class ChdSafeHandleTests
{
    /// <summary>A failed native open cannot create a SafeHandle or silently transfer context ownership.</summary>
    [Fact]
    public void NullNativeHandleRejectsAdoption()
    {
        var source = new Source();
        using (var context = new ChdSourceContext(source, leaveOpen: false))
        {
            Assert.Throws<ArgumentException>(() => new ChdSafeHandle(null, context));
            Assert.Equal(0, source.CloseCount);
        }
        Assert.Equal(1, source.CloseCount);
    }

    /// <summary>Successful native close releases the callback root and honors ownership exactly once.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulNativeCloseHonorsSourceOwnership(bool leaveOpen)
    {
        using var file = new FileChdDataSource(NativeTestEnvironment.Fixture("dvd-zstd.chd"));
        var source = new Source(file);
        using ChdSafeHandle handle = Open(source, leaveOpen);
        Assert.False(handle.IsInvalid);
        Assert.True(handle.Context.SourceLength > 0);
        handle.Dispose();
        handle.Dispose();
        Assert.True(handle.IsClosed);
        Assert.Equal(leaveOpen ? 0 : 1, source.CloseCount);
        Assert.Throws<ObjectDisposedException>(() => handle.Context.SourceLength);
        handle.Context.ThrowIfFaulted();
        if (leaveOpen)
            Assert.True(file.Length > 0);
        else
            Assert.Throws<ObjectDisposedException>(() => file.Length);
    }

    /// <summary>A native fclose fault remains contained until the managed owner requests its rethrow.</summary>
    [Fact]
    public void SuccessfulNativeCloseCapturesSourceDisposalFault()
    {
        using var file = new FileChdDataSource(NativeTestEnvironment.Fixture("dvd-zstd.chd"));
        var source = new Source(file) { CloseFault = new IOException("Close failed.") };
        using ChdSafeHandle handle = Open(source, leaveOpen: false);
        handle.Dispose();
        Assert.Equal(1, source.CloseCount);
        Assert.Same(source.CloseFault, Assert.Throws<IOException>(handle.Context.ThrowIfFaulted));
        handle.Context.ThrowIfFaulted();
    }

    /// <summary>The actual failed-open cleanup consumes callbacks once without consuming leaveOpen sources.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedNativeOpenConsumesSourceOwnership(bool leaveOpen)
    {
        _ = NativeTestEnvironment.Root;
        var source = new Source();
        using var context = new ChdSourceContext(source, leaveOpen);
        chd_file* file = null;
        chd_error error = NativeMethods.chd_open_core_file_callbacks(context.Callbacks, context.UserData, 1, null, &file);
        Assert.NotEqual(chd_error.CHDERR_NONE, error);
        Assert.True(file == null);
        Assert.Equal(leaveOpen ? 0 : 1, source.CloseCount);
        context.Dispose();
        Assert.Equal(leaveOpen ? 0 : 1, source.CloseCount);
        context.ThrowIfFaulted();
    }

    /// <summary>An abandoned handle closes its source and releases its GC-rooted context without finalizer exceptions.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void FinalizerReleasesNativeHandleAndCallbackRoot(bool leaveOpen, bool closeThrows)
    {
        using var file = new FileChdDataSource(NativeTestEnvironment.Fixture("dvd-zstd.chd"));
        var source = new Source(file)
        {
            CloseFault = closeThrows ? new IOException("Finalizer close failed.") : null
        };
        (WeakReference handle, WeakReference context) = Abandon(source, leaveOpen);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(handle.IsAlive);
        Assert.False(context.IsAlive);
        Assert.Equal(leaveOpen ? 0 : 1, source.CloseCount);
        if (leaveOpen)
            Assert.True(file.Length > 0);
        else
            Assert.Throws<ObjectDisposedException>(() => file.Length);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Handle, WeakReference Context) Abandon(Source source, bool leaveOpen)
    {
        ChdSafeHandle handle = Open(source, leaveOpen);
        return (new(handle), new(handle.Context));
    }

    private static ChdSafeHandle Open(Source source, bool leaveOpen)
    {
        var context = new ChdSourceContext(source, leaveOpen);
        chd_file* file = null;
        try
        {
            chd_error error = NativeMethods.chd_open_core_file_callbacks(context.Callbacks, context.UserData, 1, null, &file);
            context.ThrowIfFaulted();
            Assert.Equal(chd_error.CHDERR_NONE, error);
            return new(file, context);
        }
        catch
        {
            if (file != null)
                NativeMethods.chd_close(file);
            context.Dispose();
            throw;
        }
    }

    private sealed class Source(IChdDataSource? inner = null) : IChdDataSource
    {
        private int _closeCount;
        internal int CloseCount => Volatile.Read(ref _closeCount);
        internal IOException? CloseFault { get; init; }
        public long Length => inner?.Length ?? 0;
        public int Read(long offset, Span<byte> destination) => inner?.Read(offset, destination) ?? 0;
        public void Dispose()
        {
            Interlocked.Increment(ref _closeCount);
            inner?.Dispose();
            if (CloseFault is { } fault)
                throw fault;
        }
    }
}
