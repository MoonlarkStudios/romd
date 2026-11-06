using System.IO.Compression;
using NSubstitute;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Storage.Files;
using Romd.Infrastructure.Export;
using Romd.Infrastructure.Tests.Helpers;
using System.Security.Cryptography;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media;
using Romd.Storage;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Export;

public sealed class ExportArchiveWriterTests
{
    [Fact]
    public async Task WriteAsync_BoundRvzOwnershipWithdrawn_StrictFailureOrExplicitSkipNeverSubstitutes()
    {
        var bound = Sha256.Parse(new string('a', 64));
        var other = Sha256.Parse(new string('b', 64));
        var logical = Sha256.Parse(new string('c', 64));
        var target = new RomDeliveryTarget(47, 1, RomDeliveryFormat.Rvz, "disc.rvz", bound, 3,
            logical, 6, bound, 3, RomRepresentationEncoding.Rvz,
            ReadBinding: new RomManagedReadBinding(51, 61, Guid.NewGuid(), ManagedBuild));
        var catalog = Substitute.For<IRomContentCatalog>();
        var current = RvzContent(2, 52, 62, new(6, logical, Sha1.Parse(new string('d', 40)), Md5.Parse(new string('e', 32)), Crc32.FromUInt32(7)), other, 3);
        catalog.FindByRomIdAsync(47, Arg.Any<CancellationToken>()).Returns(current);
        var cas = Substitute.For<IContentAddressableStore>();
        cas.OpenRandomAccessAsync(StorageKey.FromHash(bound), Arg.Any<CancellationToken>())
            .Returns((IRandomAccessContent?)null);
        cas.OpenRandomAccessAsync(StorageKey.FromHash(other), Arg.Any<CancellationToken>()).Returns(_ => new Bytes());
        await using var pool = new RomRepresentationDecoder(new RomMediaAdapterRegistry(new FakeAdapter()),
            new() { MaxConcurrent = 4, MaxIdle = 0 });
        var writer = new ExportArchiveWriter(Substitute.For<IFileStorageService>(), new RomContentReader(catalog, cas, pool, Admission(current)));
        using var strict = new MemoryStream();
        await Should.ThrowAsync<FileNotFoundException>(() => writer.WriteAsync(strict, [new(1, "disc.rvz", 47, target)]));
        using var skipped = new MemoryStream();
        bool observedSkip = false;
        await writer.WriteAsync(skipped, [new(1, "disc.rvz", 47, target)], (_, written) =>
        {
            observedSkip = !written;
            return Task.CompletedTask;
        }, skipMissing: true);
        observedSkip.ShouldBeTrue();
        skipped.Position = 0;
        using var archive = new ZipArchive(skipped);
        archive.Entries.ShouldBeEmpty();
        await catalog.Received(2).FindByRomIdAsync(47, Arg.Any<CancellationToken>());
        await cas.DidNotReceive().OpenRandomAccessAsync(StorageKey.FromHash(other), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteAsync_DecoderPoolSaturated_WaitsThenWritesCompleteLogicalFile()
    {
        var artifactHash = Sha256.Parse(new string('a', 64));
        var identity = MediaFactFixtures.Identity(new byte[] { 1, 2, 3 });
        var current = RvzContent(1, 51, 61, identity, artifactHash, 3);
        var catalog = Substitute.For<IRomContentCatalog>();
        catalog.FindByRomIdAsync(47).Returns(current);
        var cas = Substitute.For<IContentAddressableStore>();
        cas.OpenRandomAccessAsync(StorageKey.FromHash(artifactHash)).Returns(_ => new Bytes());
        await using var pool = new RomRepresentationDecoder(new RomMediaAdapterRegistry(new FakeAdapter()), new() { MaxConcurrent = 4, MaxIdle = 0 });
        var occupied = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => pool.DecodeAsync(RomRepresentationEncoding.Rvz, new Bytes())));
        try
        {
            var writer = new ExportArchiveWriter(Substitute.For<IFileStorageService>(), new RomContentReader(catalog, cas, pool, Admission(current)));
            using var destination = new MemoryStream();
            var export = writer.WriteAsync(destination, [new(1, "disc.iso", 47)]);
            export.IsCompleted.ShouldBeFalse();
            await occupied[0].DisposeAsync();
            await export.WaitAsync(TimeSpan.FromSeconds(5));
            destination.Position = 0;
            using var archive = new ZipArchive(destination);
            using var bytes = new MemoryStream();
            await archive.Entries.Single().Open().CopyToAsync(bytes);
            bytes.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
        }
        finally { foreach (var lease in occupied) await lease.DisposeAsync(); }
    }

    [Fact]
    public async Task WriteAsync_BoundRvzWithActualDecoderPoolSaturated_OpensRawTargetAndWritesVerifiedBytes()
    {
        var artifact = Sha256.FromSpan(SHA256.HashData(new byte[] { 1, 2, 3 }));
        var identity = MediaFactFixtures.Identity(new byte[6]);
        var current = RvzContent(1, 51, 61, identity, artifact, 3);
        var target = new RomDeliveryTarget(47, 1, RomDeliveryFormat.Rvz, "disc.rvz", artifact, 3,
            identity.Sha256, identity.Size, artifact, 3, RomRepresentationEncoding.Rvz, ReadBinding: current.Preferred.BindManaged(ManagedBuild));
        var catalog = Substitute.For<IRomContentCatalog>();
        catalog.FindByRomIdAsync(47, Arg.Any<CancellationToken>()).Returns(current);
        var cas = Substitute.For<IContentAddressableStore>();
        cas.OpenRandomAccessAsync(StorageKey.FromHash(artifact), Arg.Any<CancellationToken>()).Returns(_ => new Bytes());
        var storage = Substitute.For<IFileStorageService>();
        await using var pool = new RomRepresentationDecoder(new RomMediaAdapterRegistry(new FakeAdapter()), new() { MaxConcurrent = 4, MaxIdle = 0 });
        var occupied = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => pool.DecodeAsync(RomRepresentationEncoding.Rvz, new Bytes())));
        try
        {
            using var destination = new MemoryStream();
            var writer = new ExportArchiveWriter(storage, new RomContentReader(catalog, cas, pool, Admission(current)));
            await writer.WriteAsync(destination, [new(1, "disc.rvz", 47, target)]).WaitAsync(TimeSpan.FromSeconds(5));
            destination.Position = 0;
            using var archive = new ZipArchive(destination);
            using var output = new MemoryStream();
            await archive.Entries.ShouldHaveSingleItem().Open().CopyToAsync(output);
            output.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
            await catalog.Received(1).FindByRomIdAsync(47, Arg.Any<CancellationToken>());
            storage.ReceivedCalls().ShouldBeEmpty();
        }
        finally { foreach (var lease in occupied) await lease.DisposeAsync(); }
    }

    private const string ManagedBuild = "fixture-rvz/v1";
    private static RomContentDescriptor RvzContent(int fileId, int representationId, int outputId, RomLogicalIdentity identity, Sha256 artifact, long artifactBytes)
    {
        var container = MediaFactFixtures.Rvz(identity.Size, 1, 5, 19, 131072, false);
        var proof = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), artifact, artifactBytes, identity, container,
            new RomDecoderIdentity.Managed(ManagedBuild), "whole-output/v1", "fixture-verifier", DateTimeOffset.UnixEpoch, null, null);
        var facts = new RomRepresentationFacts(representationId, fileId, artifact, artifactBytes, container, RomReconstructionManifest.WholeFile.Instance,
            [new(outputId, 47, RomOutputSelector.Whole.Instance, identity)], [proof], []);
        return new(47, outputId, identity, "disc.iso", [new(facts, outputId, true)], [], []);
    }
    private static IRomChdDeliveryAdmission Admission(RomContentDescriptor content)
    {
        var admission = Substitute.For<IRomChdDeliveryAdmission>();
        admission.AssessManaged(content.Preferred).Returns(content.Preferred.BindManaged(ManagedBuild));
        return admission;
    }

    private sealed class FakeAdapter : IRomMediaAdapter
    {
        public RomRepresentationEncoding Encoding => RomRepresentationEncoding.Rvz;
        public bool ReusesIdleImages => true;
        public ValueTask<IRandomAccessContent> OpenAsync(RomArtifactSource source, RomMediaOpenContext context,
            MediaBlockCache cache, CancellationToken ct) => source.OpenRandomAccessAsync(ct);
    }
    private sealed class Bytes : IRandomAccessContent
    {
        public long Length => 3;
        public ContentReadIntegrity Integrity => ContentReadIntegrity.None;
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            int count = (int)Math.Min(destination.Length, Length - offset);
            for (int i = 0; i < count; i++)
                destination.Span[i] = (byte)(offset + i + 1);
            return ValueTask.FromResult(count);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task WriteAsync_NestedFiles_PreservesPathsBytesAndDestination()
    {
        var storage = Substitute.For<IFileStorageService>();
        storage.RetrieveByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new MemoryStream([1, 2, 3]));
        storage.RetrieveByIdAsync(2, Arg.Any<CancellationToken>()).Returns(new MemoryStream([4]));
        using var output = new MemoryStream();
        await new ExportArchiveWriter(storage, Substitute.For<Romd.Application.Common.Content.IRomContentReader>()).WriteAsync(output, [new(1, "disc/game.bin"), new(2, "disc/game.cue")]);
        output.CanWrite.ShouldBeTrue();
        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read);
        archive.Entries.Select(e => e.FullName).ShouldBe(["disc/game.bin", "disc/game.cue"]);
        using var bytes = new MemoryStream();
        await archive.Entries[0].Open().CopyToAsync(bytes);
        bytes.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task WriteAsync_HttpLikeStream_UsesOnlyAsynchronousWrites()
    {
        var storage = Substitute.For<IFileStorageService>();
        storage.RetrieveByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new MemoryStream([1, 2, 3]));
        using var bytes = new MemoryStream();
        using var destination = new HttpLikeStream(bytes);
        await new ExportArchiveWriter(storage, Substitute.For<Romd.Application.Common.Content.IRomContentReader>()).WriteAsync(destination, [new(1, "file")]);
        bytes.Position = 0;
        using var archive = new ZipArchive(bytes);
        archive.Entries.Single().Length.ShouldBe(3);
    }

    private sealed class HttpLikeStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new InvalidOperationException("Synchronous I/O is forbidden.");
        public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous I/O is forbidden.");
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.WriteAsync(buffer, offset, count, ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("C:\\absolute")]
    [InlineData("dir/../escape")]
    [InlineData("dir//file")]
    public async Task WriteAsync_UnsafePath_RejectsBeforeReading(string path)
    {
        var storage = Substitute.For<IFileStorageService>();
        using var output = new MemoryStream();
        await Should.ThrowAsync<ArgumentException>(() => new ExportArchiveWriter(storage, Substitute.For<Romd.Application.Common.Content.IRomContentReader>()).WriteAsync(output, [new(1, path)]));
        output.Length.ShouldBe(0);
        storage.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("A/file", "a/FILE")]
    [InlineData("dir", "dir/file")]
    public void Validate_CollidingPaths_Rejects(string first, string second) =>
        Should.Throw<ArgumentException>(() => ExportArchivePaths.Validate([new(1, first), new(2, second)]));

    [Fact]
    public async Task WriteAsync_MissingFile_FailsUnlessExplicitlyAllowed()
    {
        var storage = Substitute.For<IFileStorageService>();
        using var output = new MemoryStream();
        var writer = new ExportArchiveWriter(storage, Substitute.For<Romd.Application.Common.Content.IRomContentReader>());
        await Should.ThrowAsync<FileNotFoundException>(() => writer.WriteAsync(output, [new(1, "file")]));
        using var skippedOutput = new MemoryStream();
        var skipped = false;
        await writer.WriteAsync(skippedOutput, [new(1, "file")], (_, written) => { skipped = !written; return Task.CompletedTask; }, skipMissing: true);
        skipped.ShouldBeTrue();
    }

    [Fact]
    public void Ticket_IsBoundToSnapshotAndExpires()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var tickets = new AdminDownloadTickets(time);
        var files = new List<ExportArchiveEntry> { new(1, "file") };
        var ticket = tickets.Issue(new("release.zip", files, true));
        var token = ticket.DownloadUrl.Split('/').Last();
        files.Add(new(2, "not-authorized"));
        tickets.Resolve(token)!.Entries.Count.ShouldBe(1);
        tickets.Resolve(new string('0', 64)).ShouldBeNull();
        time.Advance(TimeSpan.FromMinutes(2));
        tickets.Resolve(token).ShouldBeNull();
    }

    [Theory]
    [InlineData(false, false, "/exports/archive.zip", "archive.zip")]
    [InlineData(true, true, "/exports/archive.zip", "archive.zip")]
    [InlineData(true, false, "relative.zip", "archive.zip")]
    [InlineData(true, false, "/exports/archive.zip", "../archive.zip")]
    public void Ticket_ContradictoryOrUnsafeCompletedVariant_Rejects(bool archive, bool entries, string path, string filename)
    {
        using var tickets = new AdminDownloadTickets(TimeProvider.System);
        Should.Throw<ArgumentException>(() => tickets.Issue(new(filename,
            entries ? [new(1, "member")] : [], archive, path)));
    }
}
