using NSubstitute;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Media.Probe;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class EncodingProbeArtifactRetentionTests : IDisposable
{
    private readonly string _directory = CreateFixtureDirectory();

    [Fact]
    public async Task RetainArtifactAsync_ClosedChdExtension_PreservesExactBodyAndExistingMismatch()
    {
        var (artifact, hashes) = await FixtureAsync();
        string retained = Path.Combine(_directory, "chd-retained");
        string path = await EncodingChecks.RetainArtifactAsync(artifact, retained, hashes, ".chd");
        path.ShouldBe(Path.Combine(retained, hashes.Sha256 + ".chd"));
        (await Digests.FileAsync(path)).ShouldBe(hashes);
        (await EncodingChecks.RetainArtifactAsync(artifact, retained, hashes, ".chd")).ShouldBe(path);
        await File.WriteAllBytesAsync(path, [9, 8, 7]);
        await Should.ThrowAsync<InvalidOperationException>(() =>
            EncodingChecks.RetainArtifactAsync(artifact, retained, hashes, ".chd"));
        (await File.ReadAllBytesAsync(path)).ShouldBe(new byte[] { 9, 8, 7 });
    }

    [Theory]
    [InlineData(".iso")]
    [InlineData(".CHD")]
    [InlineData("../changed")]
    public async Task RetainArtifactAsync_UnknownExtension_RefusesBeforeCreatingDirectory(string extension)
    {
        var (artifact, hashes) = await FixtureAsync();
        string retained = Path.Combine(_directory, "not-created");
        await Should.ThrowAsync<InvalidOperationException>(() =>
            EncodingChecks.RetainArtifactAsync(artifact, retained, hashes, extension));
        Directory.Exists(retained).ShouldBeFalse();
        (await Digests.FileAsync(artifact.Path)).ShouldBe(hashes);
    }

    [Fact]
    public async Task RetainArtifactAsync_OverlappingSourceDirectory_RefusesBeforeCopyEffects()
    {
        var (artifact, hashes) = await FixtureAsync();
        string input = Path.GetDirectoryName(artifact.Path)!;
        await Should.ThrowAsync<InvalidOperationException>(() =>
            EncodingChecks.RetainArtifactAsync(artifact, input, hashes, ".chd"));
        Directory.GetFiles(input).ShouldBe(new[] { artifact.Path });
        (await Digests.FileAsync(artifact.Path)).ShouldBe(hashes);
    }

    [Fact]
    public async Task RetainArtifactAsync_NonnormalizedDirectory_RefusesBeforeCreatingDirectory()
    {
        var (artifact, hashes) = await FixtureAsync();
        string retained = Path.Combine(_directory, "absent", "..", "retained");
        await Should.ThrowAsync<InvalidOperationException>(() =>
            EncodingChecks.RetainArtifactAsync(artifact, retained, hashes, ".chd"));
        Directory.Exists(Path.Combine(_directory, "absent")).ShouldBeFalse();
        Directory.Exists(Path.Combine(_directory, "retained")).ShouldBeFalse();
    }

    [Fact]
    public async Task RetainArtifactAsync_ExactArtifact_CopiesAndReusesAllHashMatchedBytes()
    {
        var (artifact, hashes) = await FixtureAsync();
        string path = await EncodingChecks.RetainArtifactAsync(artifact, Path.Combine(_directory, "retained"), hashes);
        (await Digests.FileAsync(path)).ShouldBe(hashes);
        (await EncodingChecks.RetainArtifactAsync(artifact, Path.GetDirectoryName(path)!, hashes)).ShouldBe(path);
        Directory.GetFiles(Path.GetDirectoryName(path)!).Length.ShouldBe(1);
    }

    [Fact]
    public async Task RetainArtifactAsync_PreexistingChangedBytes_FailsWithoutOverwritingThem()
    {
        var (artifact, hashes) = await FixtureAsync();
        string path = Path.Combine(_directory, hashes.Sha256 + ".rvz");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        await Should.ThrowAsync<InvalidOperationException>(() => EncodingChecks.RetainArtifactAsync(artifact, _directory, hashes));
        (await File.ReadAllBytesAsync(path)).ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainArtifactAsync_SourceLengthDiffers_RemovesOnlyItsIncompleteCopy(bool extraByte)
    {
        var (artifact, hashes) = await FixtureAsync();
        artifact.OpenRead().Returns(_ => new MemoryStream(new byte[extraByte ? (int)hashes.Size + 1 : 1]));
        await Should.ThrowAsync<InvalidOperationException>(() => EncodingChecks.RetainArtifactAsync(artifact, _directory, hashes));
        File.Exists(Path.Combine(_directory, hashes.Sha256 + ".rvz")).ShouldBeFalse();
    }

    [Fact]
    public async Task RetainArtifactAsync_PreexistingOversizedLockedFile_RejectsItsLengthBeforeOpeningContent()
    {
        var (artifact, hashes) = await FixtureAsync();
        string path = Path.Combine(_directory, hashes.Sha256 + ".rvz");
        await using var held = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        held.SetLength(hashes.Size + 1);
        await Should.ThrowAsync<InvalidOperationException>(() => EncodingChecks.RetainArtifactAsync(artifact, _directory, hashes));
        held.Length.ShouldBe(hashes.Size + 1);
    }

    [Fact]
    public async Task StreamAsync_GrowingOrOversizedInput_ReadsAtMostOneDetectionByteBeyondItsBound()
    {
        using var source = new MemoryStream(new byte[1024 * 1024]);
        await Should.ThrowAsync<InvalidOperationException>(() => Digests.StreamAsync(source, maximumBytes: 1000));
        source.Position.ShouldBe(1001);
    }

    [Fact]
    public async Task RetainArtifactAsync_Cancellation_RemovesItsOwnedIncompleteCopy()
    {
        var (artifact, hashes) = await FixtureAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            EncodingChecks.RetainArtifactAsync(artifact, _directory, hashes, cancellation.Token));
        File.Exists(Path.Combine(_directory, hashes.Sha256 + ".rvz")).ShouldBeFalse();
    }

    [Fact]
    public async Task RetainArtifactAsync_IoFailureAfterPartialRead_RemovesItsOwnedIncompleteCopy()
    {
        var (artifact, hashes) = await FixtureAsync();
        artifact.OpenRead().Returns(_ => new FailingRead());
        await Should.ThrowAsync<IOException>(() => EncodingChecks.RetainArtifactAsync(artifact, _directory, hashes));
        File.Exists(Path.Combine(_directory, hashes.Sha256 + ".rvz")).ShouldBeFalse();
    }

    private sealed class FailingRead : MemoryStream
    {
        private bool _read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (_read)
                throw new IOException("Synthetic retention read failure.");
            _read = true;
            buffer.Span[0] = 1;
            return ValueTask.FromResult(1);
        }
    }

    private static string CreateFixtureDirectory()
    {
        string path = Directory.CreateTempSubdirectory("romd-qualified-retention-").FullName;
        // The observer requires physical paths; macOS temp directories may have a /var ancestor alias.
        for (DirectoryInfo? parent = new(path); parent is not null; parent = parent.Parent)
            if (parent.LinkTarget is not null && MediaProbePaths.Contains(parent.FullName, path))
                path = Path.Combine(parent.ResolveLinkTarget(returnFinalTarget: true)!.FullName,
                    Path.GetRelativePath(parent.FullName, path));
        return Path.GetFullPath(path);
    }

    private async Task<(ITempFile Artifact, Digests Hashes)> FixtureAsync(CancellationToken ct = default)
    {
        string source = Path.Combine(_directory + "-input", "source.data");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllBytesAsync(source, Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray(), ct);
        var hashes = await Digests.FileAsync(source, ct);
        var artifact = Substitute.For<ITempFile>();
        artifact.Path.Returns(source);
        artifact.FileSize.Returns(hashes.Size);
        artifact.FileSha256.Returns(Sha256.Parse(hashes.Sha256));
        artifact.OpenRead().Returns(_ => File.OpenRead(source));
        return (artifact, hashes);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        if (Directory.Exists(_directory + "-input"))
            Directory.Delete(_directory + "-input", recursive: true);
    }
}
