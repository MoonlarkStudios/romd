using Xunit;

namespace Moonlark.Libchdr.Tests;

/// <summary>Qualifies chd Stream Tests.</summary>
public sealed class ChdStreamTests
{
    /// <summary>Qualifies seeks And Reads Across Hunks With Normal Stream Eof.</summary>
    [Fact]
    public void SeeksAndReadsAcrossHunksWithNormalStreamEof()
    {
        using ChdFile file = NativeTestEnvironment.OpenFixture();
        using var stream = new ChdStream(file, true);
        byte[] source = File.ReadAllBytes(NativeTestEnvironment.Fixture("dvd-source.iso"));
        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Equal(source.Length, stream.Length);
        Assert.Equal(file.Header.HunkBytes - 3, stream.Seek(file.Header.HunkBytes - 3, SeekOrigin.Begin));
        byte[] range = new byte[117];
        Assert.Equal(range.Length, stream.Read(range, 0, range.Length));
        Assert.True(source.AsSpan((int)file.Header.HunkBytes - 3, range.Length).SequenceEqual(range));
        Assert.Equal(source.Length - 1, stream.Seek(-1, SeekOrigin.End));
        Assert.Equal(source[^1], stream.ReadByte());
        Assert.Equal(-1, stream.ReadByte());
        stream.Position = long.MaxValue;
        Assert.Equal(0, stream.Read(range));
        Assert.Throws<IOException>(() => stream.Seek(1, SeekOrigin.Current));
        Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Position = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(0, (SeekOrigin)7));
    }

    /// <summary>Qualifies explicit ownership; operations throw after disposal while capabilities report false, as the Stream contract requires.</summary>
    [Fact]
    public async Task ExplicitOwnershipAndAllOverriddenOperationsRespectDisposal()
    {
        using ChdFile file = NativeTestEnvironment.OpenFixture();
        var kept = new ChdStream(file, true);
        kept.Dispose();
        _ = file.Header;
        Assert.False(kept.CanRead);
        Assert.False(kept.CanSeek);
        Assert.False(kept.CanWrite);
        Assert.False(kept.CanTimeout);
        Assert.Throws<ObjectDisposedException>(() => { _ = kept.FlushAsync(); });
        Assert.True(kept.FlushAsync(new CancellationToken(canceled: true)).IsCanceled);
        Assert.Throws<ObjectDisposedException>(() => kept.ReadByte());
        Assert.Throws<ObjectDisposedException>(() => kept.Read(new byte[1].AsSpan()));
        Assert.Throws<ObjectDisposedException>(() => kept.CopyTo(Stream.Null));
        Assert.Throws<ObjectDisposedException>(() => kept.Position);
        Assert.Throws<ObjectDisposedException>(() => kept.Length);
        Assert.Throws<ObjectDisposedException>(() => kept.Flush());
        Assert.Throws<ObjectDisposedException>(() => kept.Write([]));
        Assert.Throws<ObjectDisposedException>(() => kept.SetLength(0));
        Assert.Throws<ObjectDisposedException>(() => kept.Read([]));
        Assert.Throws<ObjectDisposedException>(() => kept.Seek(0, SeekOrigin.Begin));
        Assert.Throws<ObjectDisposedException>(() => kept.ReadTimeout);
        Assert.Throws<ObjectDisposedException>(() => kept.WriteTimeout);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => kept.ReadAsync(Memory<byte>.Empty, default).AsTask());
        var owned = new ChdStream(file);
        owned.Dispose();
        Assert.Throws<ObjectDisposedException>(() => file.Header);
    }

    /// <summary>Qualifies never Queues Fake Asynchronous Native Reads.</summary>
    [Fact]
    public async Task NeverQueuesFakeAsynchronousNativeReads()
    {
        using ChdFile file = NativeTestEnvironment.OpenFixture();
        using Stream stream = new ChdStream(file, true);
        await Assert.ThrowsAsync<NotSupportedException>(() => stream.ReadAsync(Memory<byte>.Empty).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(() => stream.ReadAsync([], 0, 0));
        Assert.Throws<NotSupportedException>(() => stream.BeginRead([], 0, 0, null, null));
        await Assert.ThrowsAsync<NotSupportedException>(() => stream.CopyToAsync(Stream.Null));
        Assert.Throws<NotSupportedException>(() => stream.Write([]));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
    }

    /// <summary>Flushing a read-only stream does nothing, so FlushAsync completes synchronously like Flush, honoring cancellation.</summary>
    [Fact]
    public async Task FlushAsyncIsTheCompletedNoOpOfFlush()
    {
        using ChdFile file = NativeTestEnvironment.OpenFixture();
        using Stream stream = new ChdStream(file, true);
        Task flush = stream.FlushAsync();
        Assert.True(flush.IsCompletedSuccessfully);
        await flush;
        Assert.True(stream.FlushAsync(new CancellationToken(canceled: true)).IsCanceled);
    }

    /// <summary>Capabilities also report false once the decoder is disposed under a stream that left it open.</summary>
    [Fact]
    public void CapabilitiesReportFalseOnceTheDecoderIsDisposed()
    {
        ChdFile file = NativeTestEnvironment.OpenFixture();
        using var stream = new ChdStream(file, leaveOpen: true);
        Assert.True(stream.CanRead);
        file.Dispose();
        Assert.False(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
    }

    /// <summary>Timeouts are unsupported in the way every BCL stream reports them, with InvalidOperationException.</summary>
    [Fact]
    public void TimeoutsThrowInvalidOperationAsBclStreamsDo()
    {
        using ChdFile file = NativeTestEnvironment.OpenFixture();
        using var stream = new ChdStream(file, true);
        Assert.Throws<InvalidOperationException>(() => stream.ReadTimeout);
        Assert.Throws<InvalidOperationException>(() => stream.ReadTimeout = 1);
        Assert.Throws<InvalidOperationException>(() => stream.WriteTimeout);
        Assert.Throws<InvalidOperationException>(() => stream.WriteTimeout = 1);
    }
}
