using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using NSubstitute;
using Romd.Application.Common.Content;
using Romd.Consumer.Application.Delivery;
using Romd.Domain.Hashing;
using Romd.Infrastructure.Delivery;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Delivery;

public sealed class ConsumerDiscSetArchiveTests
{
    [Fact]
    public void Tickets_ExpiryAndDifferentHost_RefuseTheCapability()
    {
        var time = new Clock();
        using var tickets = new ConsumerDiscSetDownloadTickets(time);
        using var otherHost = new ConsumerDiscSetDownloadTickets(time);
        var plan = Plan("hello"u8.ToArray());
        var issued = tickets.Issue(plan);
        string token = issued.DownloadUrl.Split('/')[^1];
        tickets.Resolve(token).ShouldBeSameAs(plan);
        otherHost.Resolve(token).ShouldBeNull();
        time.Now = issued.ExpiresAt;
        tickets.Resolve(token).ShouldBeNull();
    }

    [Fact]
    public async Task WriteAsync_HttpLikeDestination_StreamsFilesAndExactUtf8Playlist()
    {
        byte[] bytes = new byte[200_000];
        RandomNumberGenerator.Fill(bytes);
        var artifacts = Substitute.For<IConsumerContentArtifactResolver>();
        var plan = Plan(bytes);
        artifacts.ResolveAsync(plan.Entries[0].Sha256, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<Stream?>(new MemoryStream(bytes)));
        var writer = new ConsumerDiscSetArchiveWriter(Substitute.For<IRomContentReader>(), artifacts, Substitute.For<IRomCdArchiveDownloadReader>());
        using var destination = new MemoryStream();
        await writer.WriteAsync(new AsyncDestination(destination), plan);
        destination.Position = 0;
        using var zip = new ZipArchive(destination);
        await using var file = zip.GetEntry("disc-01/game.cue")!.Open();
        using var copy = new MemoryStream();
        await file.CopyToAsync(copy);
        copy.ToArray().ShouldBe(bytes);
        await using var playlist = zip.GetEntry("playlist.m3u")!.Open();
        copy.SetLength(0);
        await playlist.CopyToAsync(copy);
        copy.ToArray().ShouldBe(Encoding.UTF8.GetBytes(plan.Playlist));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("truncated")]
    [InlineData("oversized")]
    [InlineData("corrupt")]
    public async Task WriteAsync_ChangedOrMissingSelectedBytes_FailsWithoutSubstituting(string change)
    {
        var plan = Plan("hello"u8.ToArray());
        byte[]? bytes = change switch { "missing" => null, "truncated" => "hell"u8.ToArray(), "oversized" => "hello!"u8.ToArray(), _ => "wrong"u8.ToArray() };
        var artifacts = Substitute.For<IConsumerContentArtifactResolver>();
        artifacts.ResolveAsync(plan.Entries[0].Sha256, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<Stream?>(bytes is null ? null : new MemoryStream(bytes)));
        var writer = new ConsumerDiscSetArchiveWriter(Substitute.For<IRomContentReader>(), artifacts, Substitute.For<IRomCdArchiveDownloadReader>());
        if (change == "missing")
            await Should.ThrowAsync<FileNotFoundException>(() => writer.WriteAsync(new MemoryStream(), plan));
        else
            await Should.ThrowAsync<InvalidDataException>(() => writer.WriteAsync(new MemoryStream(), plan));
    }

    private static ConsumerDiscSetArchive Plan(byte[] bytes) => new("game.zip", "disc-01/game.cue\n",
        [new("disc-01/game.cue", Sha256.FromBytes(SHA256.HashData(bytes)), bytes.Length, new ConsumerDiscSetArchiveSource.Descriptor())]);

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class AsyncDestination(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new InvalidOperationException("Synchronous flush.");
        public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous write.");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
