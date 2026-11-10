using Romd.Media.Cd;
using Romd.Media.Chd;
using Romd.Media.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests.Cd;

public sealed class ChdCdBinContentTests
{
    [Theory]
    [InlineData("single", true)]
    [InlineData("multi", true)]
    [InlineData("multi", false)]
    [InlineData("partial", true)]
    public async Task ReadAtAsync_ReconstructsOriginalFilesAndArbitraryRanges(string shape, bool concatenate)
    {
        ChdNativeCandidate.FromBuildRecord(NativeTestBuilds.LibchdrDirectory).VerifyAndLoad();
        int count = shape == "partial" ? 2 : 3;
        ChdCdBinExtent[] extents = [new(0, 17, false), new(20, 13, true), new(36, 9, true)];
        await using var pool = new ChdReadPool(new MediaBlockCache(1 << 20), 2, 1);
        foreach (int index in concatenate ? new[] { -1 } : Enumerable.Range(0, count))
        {
            var device = await ChdBlockDevice.OpenAsync(new RawFile(Fixture($"cd-edge-{shape}.chd")), pool, ChdCdLimits.Preflight);
            await using var member = await ChdCdBinContent.CreateAsync(device, index < 0 ? extents[..count] : [extents[index]]);
            byte[] expected = (index < 0 ? Enumerable.Range(1, count) : [index + 1])
                .SelectMany(n => File.ReadAllBytes(Fixture($"cd-edge-track{n}.bin"))).ToArray();
            member.Length.ShouldBe(expected.Length);
            byte[] actual = new byte[expected.Length];
            await member.ReadExactlyAtAsync(0, actual);
            actual.ShouldBe(expected); // independent original BIN bytes, not expected values derived from the projection.
            long[] offsets = [0, 1, 2351, 2352, 17 * 2352 - 3, 17 * 2352 + 1, expected.Length - 5, expected.Length, long.MaxValue];
            foreach (long offset in offsets)
            {
                byte[] range = Enumerable.Repeat((byte)0xfe, 8077).ToArray();
                int read = await member.ReadAtAsync(offset, range);
                int length = offset < expected.Length ? (int)Math.Min(8077, expected.Length - offset) : 0;
                read.ShouldBe(length);
                range[..read].ShouldBe(length == 0 ? [] : expected.AsSpan((int)offset, length).ToArray());
                range.AsSpan(read).IndexOfAnyExcept((byte)0xfe).ShouldBe(-1);
            }
        }
    }

    [Fact]
    public async Task ReadAtAsync_CancellationAndInvalidOffset_PreserveDestination()
    {
        var raw = new MemoryContent(new byte[2448 * 4]);
        await using var member = await ChdCdBinContent.CreateAsync(raw, [new(0, 4, true)]);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        byte[] buffer = [0xfe];
        try
        { await member.ReadAtAsync(1, buffer, cancel.Token); Assert.Fail("Expected cancellation."); }
        catch (OperationCanceledException error) { error.CancellationToken.ShouldBe(cancel.Token); }
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => member.ReadAtAsync(-1, buffer).AsTask());
        buffer.ShouldBe(new byte[] { 0xfe });
        raw.Reads.ShouldBe(0);
    }

    [Fact]
    public async Task CreateAsync_InvalidRecipe_DisposesSource()
    {
        foreach (var extents in new ChdCdBinExtent[][] { [], [new(-1, 1, false)], [new(0, 0, false)],
                     [new(0, 5, false)], [new(2, 2, false), new(1, 1, true)] })
        {
            var raw = new MemoryContent(new byte[2448 * 4]);
            await Should.ThrowAsync<InvalidDataException>(() => ChdCdBinContent.CreateAsync(raw, extents).AsTask());
            raw.Disposed.ShouldBeTrue();
        }
    }

    private static string Fixture(string name) => NativeTestBuilds.LibchdrFixture(name);
    private sealed class RawFile(string path) : ISynchronousRawContent
    {
        private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _handle = File.OpenHandle(path);
        public long Length { get; } = new FileInfo(path).Length;
        public int ReadAt(long offset, Span<byte> destination) => RandomAccess.Read(_handle, destination, offset);
        public void Dispose() => _handle.Dispose();
    }
    private sealed class MemoryContent(byte[] data) : IRandomAccessContent
    {
        public bool Disposed { get; private set; }
        public int Reads { get; private set; }
        public long Length => data.Length;
        public ContentReadIntegrity Integrity => ContentReadIntegrity.None;
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        { Reads++; int count = (int)Math.Min(destination.Length, data.Length - offset); data.AsMemory((int)offset, count).CopyTo(destination); return new(count); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
