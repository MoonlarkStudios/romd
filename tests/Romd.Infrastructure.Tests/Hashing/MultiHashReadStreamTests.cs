using Romd.Admin.Application.Hashing;
using Romd.Infrastructure.Hashing;
using Romd.Storage.Tests;
using Xunit;

namespace Romd.Infrastructure.Tests.Hashing;

public class MultiHashReadStreamTests
{
    [Theory]
    [MemberData(nameof(TestContents.All), MemberType = typeof(TestContents))]
    public async Task AllHashes_MatchExpectedValues(ContentTestData content)
    {
        await using var inner = content.OpenStream();
        using var stream = new MultiHashReadStream(inner, HashAlgorithms.All);

        await stream.CopyToAsync(Stream.Null);

        var hashes = stream.GetComputedHashes();

        Assert.Equal(content.Sha256, hashes.Sha256.ToString());
        Assert.Equal(content.Sha1, hashes.Sha1.ToString());
        Assert.Equal(content.Md5, hashes.Md5.ToString());
        Assert.Equal(content.Crc32, hashes.Crc32.ToString());
    }

    [Theory]
    [MemberData(nameof(TestContents.All), MemberType = typeof(TestContents))]
    public async Task DatHashesOnly_OmitsSha256(ContentTestData content)
    {
        await using var inner = content.OpenStream();
        using var stream = new MultiHashReadStream(inner, HashAlgorithms.DatHashes);

        await stream.CopyToAsync(Stream.Null);

        var hashes = stream.GetComputedHashes();

        Assert.True(hashes.Sha256.IsEmpty);
        Assert.Equal(content.Sha1, hashes.Sha1.ToString());
        Assert.Equal(content.Md5, hashes.Md5.ToString());
        Assert.Equal(content.Crc32, hashes.Crc32.ToString());
    }

    [Theory]
    [MemberData(nameof(TestContents.All), MemberType = typeof(TestContents))]
    public async Task Sha256Only_OmitsDatHashes(ContentTestData content)
    {
        await using var inner = content.OpenStream();
        using var stream = new MultiHashReadStream(inner, HashAlgorithms.Sha256);

        await stream.CopyToAsync(Stream.Null);

        var hashes = stream.GetComputedHashes();

        Assert.Equal(content.Sha256, hashes.Sha256.ToString());
        Assert.True(hashes.Sha1.IsEmpty);
        Assert.True(hashes.Md5.IsEmpty);
        Assert.True(hashes.Crc32.IsEmpty);
    }

    [Fact]
    public async Task GetComputedHashes_CalledTwice_Throws()
    {
        using var inner = new MemoryStream([1, 2, 3]);
        using var stream = new MultiHashReadStream(inner, HashAlgorithms.All);

        await stream.CopyToAsync(Stream.Null);

        _ = stream.GetComputedHashes(); // First call succeeds

        Assert.Throws<InvalidOperationException>(() => stream.GetComputedHashes());
    }

    [Fact]
    public void Constructor_NonReadableStream_ThrowsArgumentException()
    {
        using var nonReadable = new NonReadableStream();

        Assert.Throws<ArgumentException>(() => new MultiHashReadStream(nonReadable, HashAlgorithms.All));
    }

    [Fact]
    public void Constructor_NullStream_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new MultiHashReadStream(null!, HashAlgorithms.All));
    }

    [Theory]
    [MemberData(nameof(TestContents.All), MemberType = typeof(TestContents))]
    public async Task BytesRead_TracksCorrectly(ContentTestData content)
    {
        await using var inner = content.OpenStream();
        using var stream = new MultiHashReadStream(inner, HashAlgorithms.All);

        await stream.CopyToAsync(Stream.Null);

        Assert.Equal(content.Size, stream.BytesRead);
    }

    [Fact]
    public async Task LeaveOpen_False_DisposesInner()
    {
        var inner = new DisposeTrackingStream(new MemoryStream([1, 2, 3]));
        using (var stream = new MultiHashReadStream(inner, HashAlgorithms.All, leaveOpen: false))
        {
            await stream.CopyToAsync(Stream.Null);
        }

        Assert.True(inner.WasDisposed);
    }

    [Fact]
    public async Task LeaveOpen_True_DoesNotDisposeInner()
    {
        var inner = new DisposeTrackingStream(new MemoryStream([1, 2, 3]));
        using (var stream = new MultiHashReadStream(inner, HashAlgorithms.All, leaveOpen: true))
        {
            await stream.CopyToAsync(Stream.Null);
        }

        Assert.False(inner.WasDisposed);

        inner.Dispose(); // Manual cleanup
    }

    private sealed class NonReadableStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    private sealed class DisposeTrackingStream(Stream inner) : Stream
    {
        public bool WasDisposed { get; private set; }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => inner.Read(buffer, offset, count);

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                WasDisposed = true;
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
