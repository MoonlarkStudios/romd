using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Vfs;

public sealed class VfsProbeDiagnosticsTests
{
    [Fact]
    public async Task DrainAsync_LargeDebugStream_DrainsToEofAndRetainsCompleteBoundedLines()
    {
        using var source = new ChunkedReader(string.Concat(Enumerable.Repeat("DEBUG credential=value\n", 100_000)), 71);
        int redactedLines = 0;
        var capture = await VfsProbeDiagnostics.DrainAsync(source, line =>
        {
            line.ShouldNotContain('\n');
            redactedLines++;
            return line.Replace("value", "<redacted>", StringComparison.Ordinal);
        }, CancellationToken.None);

        source.SawEof.ShouldBeTrue();
        source.MaximumRequested.ShouldBeLessThanOrEqualTo(4096);
        redactedLines.ShouldBe(100_000);
        capture.Text.Length.ShouldBeLessThanOrEqualTo(VfsProbeDiagnostics.MaximumRetainedCharacters);
        capture.Text.ShouldStartWith("DEBUG credential=<redacted>\n");
        capture.Text.ShouldEndWith("DEBUG credential=<redacted>\n");
        capture.Text.ShouldNotContain("value");
        capture.DroppedLines.ShouldBe(0);
    }

    [Fact]
    public async Task DrainAsync_SecretCrossesReadBoundaries_RedactsWholeLinesIncludingUnterminatedEof()
    {
        const string secret = "probe-password-very-private";
        using var source = new ChunkedReader("prefix " + secret + " suffix\r\n" + secret, 3);
        var capture = await VfsProbeDiagnostics.DrainAsync(source,
            line => line.Replace(secret, "<redacted>", StringComparison.Ordinal), CancellationToken.None);

        capture.Text.ShouldBe("prefix <redacted> suffix\n<redacted>");
        capture.Text.ShouldNotContain(secret);
        source.SawEof.ShouldBeTrue();
    }

    [Fact]
    public async Task DrainAsync_OversizedLine_DropsEntireLineAndContinuesDraining()
    {
        string oversized = new('x', 2 * VfsProbeDiagnostics.MaximumRetainedCharacters);
        using var source = new ChunkedReader("safe-before\n" + oversized + "sensitive-tail\n" + oversized + "sensitive-eof", 17);
        var capture = await VfsProbeDiagnostics.DrainAsync(source, line => line, CancellationToken.None);

        capture.Text.ShouldBe("safe-before\n");
        capture.DroppedLines.ShouldBe(2);
        source.SawEof.ShouldBeTrue();
    }

    [Fact]
    public async Task DrainAsync_RedactionExpandsPastBound_DropsWholeResultWithoutTruncating()
    {
        using var source = new ChunkedReader("expand\nsafe\n", 2);
        var capture = await VfsProbeDiagnostics.DrainAsync(source,
            line => line == "expand" ? new string('s', VfsProbeDiagnostics.MaximumRetainedCharacters + 1) : line,
            CancellationToken.None);

        capture.Text.ShouldBe("safe\n");
        capture.DroppedLines.ShouldBe(1);
    }

    [Fact]
    public async Task DrainAsync_RedactorFailure_DropsUnsafeLineAndContinuesDrainingWithTypeOnlyDiagnostic()
    {
        using var source = new ChunkedReader("private-line\nsafe\n", 2);
        var capture = await VfsProbeDiagnostics.DrainAsync(source,
            line => line == "private-line" ? throw new InvalidOperationException("secret-in-message") : line,
            CancellationToken.None);

        capture.Text.ShouldContain(nameof(InvalidOperationException));
        capture.Text.ShouldContain("safe\n");
        capture.Text.ShouldNotContain("private-line");
        capture.Text.ShouldNotContain("secret-in-message");
        capture.DroppedLines.ShouldBe(1);
        source.SawEof.ShouldBeTrue();
    }

    [Fact]
    public async Task DrainAsync_Cancellation_JoinsTheOwnedRead()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var source = new BlockedReader();
        var draining = VfsProbeDiagnostics.DrainAsync(source, line => line, canceled.Token);
        await source.Entered.Task.WaitAsync(deadline.Token);
        await canceled.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await draining);
        source.Joined.ShouldBeTrue();
    }

    private sealed class ChunkedReader(string text, int maximumRead) : TextReader
    {
        private int _position;
        public int MaximumRequested { get; private set; }
        public bool SawEof { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            MaximumRequested = Math.Max(MaximumRequested, buffer.Length);
            int count = Math.Min(Math.Min(maximumRead, buffer.Length), text.Length - _position);
            text.AsSpan(_position, count).CopyTo(buffer.Span);
            _position += count;
            SawEof |= count == 0;
            return ValueTask.FromResult(count);
        }

        public override Task<string> ReadToEndAsync(CancellationToken ct) =>
            throw new InvalidOperationException("Unbounded diagnostic reads are forbidden by this fixture.");
    }

    private sealed class BlockedReader : TextReader
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Joined { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            try
            { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { Joined = true; }
            return 0;
        }

        public override Task<string> ReadToEndAsync(CancellationToken ct) =>
            throw new InvalidOperationException("Unbounded diagnostic reads are forbidden by this fixture.");
    }
}
