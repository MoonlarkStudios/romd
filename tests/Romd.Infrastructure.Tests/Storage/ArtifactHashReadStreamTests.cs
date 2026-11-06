using System.Security.Cryptography;
using Romd.Domain.Hashing;
using Romd.Infrastructure.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class ArtifactHashReadStreamTests
{
    [Fact]
    public async Task ReadAsync_PartialReadThenRestart_MeasuresWholeArtifactOnce()
    {
        byte[] bytes = [1, 3, 5, 7, 9];
        using var source = new MemoryStream(bytes);
        using var measured = new ArtifactHashReadStream(source);
        (await measured.ReadAsync(new byte[2])).ShouldBe(2);
        measured.Hashes.ShouldBeNull();
        measured.Position = 0;

        await measured.CopyToAsync(Stream.Null);

        AssertMeasurement(measured, bytes);
    }

    [Fact]
    public async Task ReadAsync_CompleteReadThenCasFallback_DoesNotAppendSecondPass()
    {
        byte[] bytes = [1, 3, 5, 7, 9];
        using var source = new MemoryStream(bytes);
        using var measured = new ArtifactHashReadStream(source);
        await measured.CopyToAsync(Stream.Null);
        measured.Position = 0;

        await measured.CopyToAsync(Stream.Null);

        AssertMeasurement(measured, bytes);
        source.CanRead.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadAsync_PartialReadThenArbitrarySeek_RejectsMixedMeasurement()
    {
        using var measured = new ArtifactHashReadStream(new MemoryStream([1, 2, 3]));
        (await measured.ReadAsync(new byte[1])).ShouldBe(1);

        Should.Throw<InvalidDataException>(() => measured.Position = 2);

        measured.Hashes.ShouldBeNull();
    }

    [Fact]
    public async Task ReadAsync_ZeroLengthRead_DoesNotFinalizePartialMeasurement()
    {
        byte[] bytes = [1, 2, 3];
        using var measured = new ArtifactHashReadStream(new MemoryStream(bytes));
        (await measured.ReadAsync(new byte[1])).ShouldBe(1);

        (await measured.ReadAsync(Memory<byte>.Empty)).ShouldBe(0);

        measured.Hashes.ShouldBeNull();
        await measured.CopyToAsync(Stream.Null);
        AssertMeasurement(measured, bytes);
    }

    [Fact]
    public async Task ReadAsync_SourceStartsAfterPrefix_MeasuresOnlyDeliveredBytesAndRetainsOwnership()
    {
        using var source = new MemoryStream([0xff, 1, 2, 3]);
        source.Position = 1;
        using (var measured = new ArtifactHashReadStream(source))
        {
            await measured.CopyToAsync(Stream.Null);
            AssertMeasurement(measured, [1, 2, 3]);
        }
        source.CanRead.ShouldBeTrue();
    }

    [Fact]
    public async Task EnsureMeasuredAsync_UnexpectedLength_RejectsUnsafeData()
    {
        using var measured = new ArtifactHashReadStream(new MemoryStream(new byte[200_000]));

        await Should.ThrowAsync<InvalidDataException>(() => measured.EnsureMeasuredAsync(100, CancellationToken.None));

        measured.Hashes.ShouldBeNull();
    }

    [Fact]
    public async Task EnsureMeasuredAsync_Cancelled_DoesNotFinalizeMeasurement()
    {
        using var measured = new ArtifactHashReadStream(new MemoryStream([1, 2, 3]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => measured.EnsureMeasuredAsync(3, cancellation.Token));

        measured.Hashes.ShouldBeNull();
    }

    [Fact]
    public async Task EnsureMeasuredAsync_NonSeekablePartialCopy_ContinuesSamePassWithoutLosingPrefix()
    {
        byte[] bytes = [1, 3, 5, 7, 9];
        using var source = new NonSeekableStream(bytes);
        using var measured = new ArtifactHashReadStream(source);
        (await measured.ReadAsync(new byte[2])).ShouldBe(2);
        measured.Hashes.ShouldBeNull();

        await measured.EnsureMeasuredAsync(bytes.Length, CancellationToken.None);

        AssertMeasurement(measured, bytes);
    }

    [Fact]
    public async Task EnsureMeasuredAsync_NonSeekableSkippedCopy_MeasuresWholeArtifact()
    {
        byte[] bytes = [1, 3, 5, 7, 9];
        using var measured = new ArtifactHashReadStream(new NonSeekableStream(bytes));

        await measured.EnsureMeasuredAsync(bytes.Length, CancellationToken.None);

        AssertMeasurement(measured, bytes);
    }

    private static void AssertMeasurement(ArtifactHashReadStream measured, byte[] bytes)
    {
        measured.MeasuredSize.ShouldBe(bytes.Length);
        measured.Hashes!.Value.Sha256.ShouldBe(Sha256.FromSpan(SHA256.HashData(bytes)));
        measured.Hashes.Value.Md5.ShouldBe(Md5.FromSpan(MD5.HashData(bytes)));
        measured.Hashes.Value.Sha1.ShouldBe(Sha1.FromSpan(SHA1.HashData(bytes)));
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Length => throw new NotSupportedException();
    }
}
