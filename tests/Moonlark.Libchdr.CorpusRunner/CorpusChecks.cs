using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Moonlark.Libchdr.CorpusRunner;

/// <summary>The process's peak memory: the runtime's peak working set, or on macOS, where the runtime reports zero, the
/// lifetime maximum physical footprint, which also counts compressed pages.</summary>
internal static class ResourceUsage
{
    private const int RusageInfoV4 = 4;
    // ri_lifetime_max_phys_footprint in struct rusage_info_v4, counted in 8-byte words: a 16-byte UUID, then 28 counters.
    private const int LifetimeMaxPhysicalFootprint = 30;

    internal static long PeakWorkingSet()
    {
        long peak = Process.GetCurrentProcess().PeakWorkingSet64;
        if (peak != 0 || !OperatingSystem.IsMacOS())
            return peak;
        long[] usage = new long[64];
        return GetProcessResourceUsage(Environment.ProcessId, RusageInfoV4, usage) == 0 ? usage[LifetimeMaxPhysicalFootprint] : 0;
    }

    [DllImport("libc", EntryPoint = "proc_pid_rusage")]
    private static extern int GetProcessResourceUsage(int pid, int flavor, [Out] long[] usage);
}

/// <summary>A check that failed; its message is the input's result line.</summary>
internal sealed class CorpusFailure(string message) : Exception(message);

/// <summary>The checks one possibly malformed CHD must pass, each failing with a <see cref="CorpusFailure"/>.</summary>
internal static class CorpusChecks
{
    internal const string PeakWorkingSetLine = "#peak-working-set";
    private const ulong WholeFileReadAheadBytes = 1 << 20;
    private const int RedzoneBytes = 64;
    private const byte Canary = 0xA5;
    private const int StackBytes = 1 << 20;
    private const int HunkBudget = 8192;

    /// <summary>Runs every check on a thread with a 1 MiB stack, so unbounded native recursion in any pass overflows and
    /// crashes the process; returns "rejected &lt;error&gt;" or "read &lt;summary&gt;".</summary>
    internal static string Exercise(string path)
    {
        string result = "";
        Exception? escaped = null;
        var thread = new Thread(() =>
        {
            try
            { result = ExerciseOnThisThread(path); }
            catch (Exception error) { escaped = error; }
        }, StackBytes);
        thread.Start();
        thread.Join();
        if (escaped is not null)
            ExceptionDispatchInfo.Throw(escaped);
        return result;
    }

    private static string ExerciseOnThisThread(string path)
    {
        Decoded plain = Decode(path, readAheadBytes: 0, everyPath: true);
        CompareReadAhead(plain, Decode(path, WholeFileReadAheadBytes, everyPath: false), "a 1 MiB window");
        if (plain.Rejected is { } error)
            return error == ChdError.None ? "untyped rejection reported ChdError.None" : $"rejected {error}";
        // A window of one hunk refills and slides on almost every read, where a 1 MiB window holds a whole corpus input.
        CompareReadAhead(plain, Decode(path, plain.HunkBytes, everyPath: false), "a one-hunk window");
        DecodeInReverse(path, plain);
        return $"read {plain.Summary}";
    }

    /// <summary>Decodes every hunk, or a deterministic sample of <see cref="HunkBudget"/> hunks when a malformed header opens
    /// with more, so a pass stays far inside the harness's timeouts.</summary>
    private static Decoded Decode(string path, ulong readAheadBytes, bool everyPath)
    {
        if (!ChdFile.TryOpen(path, out ChdFile? file, out ChdError error, new ChdOpenOptions { ReadAheadBytes = readAheadBytes }))
            return new(error, [], [], 0, "");
        using (file)
        {
            uint[] indices = Indices(file.Header.HunkCount);
            var buffers = new HunkBuffers((int)file.Header.HunkBytes);
            var hunks = new string[indices.Length];
            var good = new GoodHunk((int)file.Header.HunkBytes);
            int failed = 0;
            for (int at = 0; at < indices.Length; at++)
            {
                hunks[at] = ReadTwice(file, indices[at], buffers, readAheadBytes != 0);
                bool reads = hunks[at].StartsWith("ok ", StringComparison.Ordinal);
                if (!reads)
                    failed++;
                if (!everyPath)
                    continue;
                if (reads)
                { CompareRangeReads(file, indices[at], buffers.First); good.Remember(indices[at], buffers.First); }
                else
                    RangeReadsFailAndSpareTheCache(file, indices[at], good);
            }
            string sampled = indices.Length < file.Header.HunkCount ? $" sampled={indices.Length}/{file.Header.HunkCount}" : "";
            string summary = everyPath
                ? $"failed-hunks={failed}{sampled} metadata-failed={Metadata(file)} cd={Cd(file)} integrity={Integrity(file)}"
                : "";
            return new(null, indices, hunks, file.Header.HunkBytes, summary);
        }
    }

    /// <summary>Every hunk when there are at most <see cref="HunkBudget"/>; otherwise the first half of the budget in order,
    /// then the rest spread evenly to the last hunk.</summary>
    private static uint[] Indices(uint count)
    {
        if (count <= HunkBudget)
            return [.. Enumerable.Range(0, (int)count).Select(index => (uint)index)];
        const int contiguous = HunkBudget / 2;
        ulong span = count - contiguous - 1;
        return [.. Enumerable.Range(0, contiguous).Select(index => (uint)index),
            .. Enumerable.Range(1, HunkBudget - contiguous).Select(step => (uint)(contiguous + span * (ulong)step / (HunkBudget - contiguous)))];
    }

    /// <summary>Decodes a hunk after 0x00 and 0xFF prefills, judging each read on its own, with a canary after the
    /// destination; returns "ok &lt;sha-256&gt;" or "error &lt;error&gt;".</summary>
    private static string ReadTwice(ChdFile file, uint index, HunkBuffers buffers, bool readAhead)
    {
        string zeros = ReadOnce(file, index, buffers.FirstMemory, 0x00);
        string ones = ReadOnce(file, index, buffers.SecondMemory, 0xFF);
        if (!buffers.Intact)
            throw new CorpusFailure($"overrun hunk {index} wrote past its {buffers.First.Length}-byte destination");
        if (zeros != ones)
            throw new CorpusFailure(readAhead
                ? $"state-dependent hunk {index} with read-ahead: {zeros} on the first read (0x00 prefill), {ones} on the second (0xFF)"
                : $"nondeterministic hunk {index}: {zeros} after a 0x00 prefill, {ones} after 0xFF");
        if (zeros != "ok")
            return zeros;
        if (!buffers.First.SequenceEqual(buffers.Second))
            throw new CorpusFailure($"nondeterministic hunk {index} depends on prior destination contents");
        return "ok " + Convert.ToHexStringLower(SHA256.HashData(buffers.First));
    }

    private static string ReadOnce(ChdFile file, uint index, Span<byte> memory, byte prefill)
    {
        Span<byte> destination = memory[..^RedzoneBytes];
        destination.Fill(prefill);
        memory[^RedzoneBytes..].Fill(Canary);
        try
        { file.ReadHunk(index, destination); return "ok"; }
        catch (ChdException failure) when (failure.Error != ChdError.None) { return $"error {failure.Error}"; }
    }

    /// <summary>The unaligned range inside a hunk that ReadAt and ChdStream read: from its second byte to its last logical byte.</summary>
    private static (long Start, int Length) Range(ChdFile file, uint index)
    {
        ulong start = (ulong)index * file.Header.HunkBytes;
        if (start >= file.Header.LogicalBytes || start >= long.MaxValue)
            return (0, 0);
        return ((long)start + 1, (int)Math.Min(file.Header.HunkBytes, file.Header.LogicalBytes - start) - 1);
    }

    /// <summary>ReadAt and ChdStream must return ReadHunk's bytes.</summary>
    private static void CompareRangeReads(ChdFile file, uint index, ReadOnlySpan<byte> hunk)
    {
        (long start, int length) = Range(file, index);
        if (length <= 0)
            return;
        ReadOnlySpan<byte> expected = hunk.Slice(1, length);
        byte[] buffer = new byte[length];
        try
        {
            if (file.ReadAt(start, buffer) != length || !expected.SequenceEqual(buffer))
                throw new CorpusFailure($"divergent ReadAt in hunk {index}");
            Array.Clear(buffer);
            using var stream = new ChdStream(file, leaveOpen: true) { Position = start };
            stream.ReadExactly(buffer);
            if (!expected.SequenceEqual(buffer))
                throw new CorpusFailure($"divergent ChdStream in hunk {index}");
        }
        catch (Exception failure) when (failure is ChdException or EndOfStreamException)
        {
            throw new CorpusFailure($"divergent range read in hunk {index}: ReadHunk succeeds, a range read fails with {failure.Message}");
        }
    }

    /// <summary>Range reads of a failing hunk must fail too, and the hunk cache must still hold the last good hunk's bytes
    /// afterwards, however much of the failing hunk was decoded into it.</summary>
    private static void RangeReadsFailAndSpareTheCache(ChdFile file, uint index, GoodHunk good)
    {
        (long start, int length) = Range(file, index);
        if (length > 0)
        {
            byte[] buffer = new byte[length];
            if (Succeeds(() => file.ReadAt(start, buffer)))
                throw new CorpusFailure($"divergent ReadAt in hunk {index}: it succeeds where ReadHunk fails");
            using var stream = new ChdStream(file, leaveOpen: true) { Position = start };
            if (Succeeds(() => stream.ReadExactly(buffer)))
                throw new CorpusFailure($"divergent ChdStream in hunk {index}: it succeeds where ReadHunk fails");
        }
        if (good.Index is { } last)
        {
            try
            { CompareRangeReads(file, last, good.Bytes); }
            catch (CorpusFailure failure) { throw new CorpusFailure($"{failure.Message}, read again after hunk {index} failed"); }
        }
    }

    private static bool Succeeds(Action read)
    {
        try
        { read(); return true; }
        catch (ChdException failure) when (failure.Error != ChdError.None) { return false; }
    }

    private static void CompareReadAhead(Decoded plain, Decoded ahead, string window)
    {
        if (plain.Rejected != ahead.Rejected)
            throw new CorpusFailure($"divergent read-ahead: {Describe(plain)} without it, {Describe(ahead)} with {window}");
        for (int at = 0; at < plain.Hunks.Length; at++)
            if (plain.Hunks[at] != ahead.Hunks[at])
                throw new CorpusFailure($"divergent read-ahead at hunk {plain.Indices[at]}: {plain.Hunks[at]} without it, {ahead.Hunks[at]} with {window}");
    }

    private static string Describe(Decoded decoded) => decoded.Rejected is { } error ? $"rejected {error}" : "opens";

    /// <summary>Decodes the same hunks again in reverse order on a fresh handle, where a deep reference chain cannot lean on
    /// libchdr's cache of the previous hunk; every hunk must keep its ascending outcome.</summary>
    private static void DecodeInReverse(string path, Decoded ascending)
    {
        using ChdFile file = ChdFile.Open(path);
        var buffers = new HunkBuffers((int)file.Header.HunkBytes);
        for (int at = ascending.Indices.Length; at-- > 0;)
        {
            uint index = ascending.Indices[at];
            string outcome;
            try
            { outcome = ReadTwice(file, index, buffers, readAhead: false); }
            catch (CorpusFailure failure) { throw new CorpusFailure(failure.Message + " (in reverse order)"); }
            if (outcome != ascending.Hunks[at])
                throw new CorpusFailure($"order-dependent hunk {index}: {ascending.Hunks[at]} in ascending order, {outcome} in reverse");
        }
    }

    /// <summary>Reads every metadata payload the enumeration lists; returns how many failed with ChdException.</summary>
    private static int Metadata(ChdFile file)
    {
        var occurrences = new Dictionary<ChdMetadataTag, uint>();
        int failed = 0;
        foreach (ChdMetadataInfo entry in file.EnumerateMetadata())
        {
            occurrences.TryGetValue(entry.Tag, out uint occurrence);
            occurrences[entry.Tag] = occurrence + 1;
            try
            {
                if (!file.TryGetMetadata(entry.Tag, occurrence, new byte[entry.Length], out _))
                    throw new CorpusFailure($"divergent metadata: {entry.Tag} entry {occurrence} is enumerated but not found");
            }
            catch (ChdException failure) when (failure.Error != ChdError.None) { failed++; }
        }
        return failed;
    }

    /// <summary>Opens the CD view when the unit size is a CD frame; each frame must equal its hunk's bytes, and each
    /// sector projection must succeed, be unsupported for its track, or fail with ChdException.</summary>
    private static string Cd(ChdFile file)
    {
        if (file.Header.UnitBytes != ChdCdImage.FrameBytes)
            return "none";
        ChdCdImage image;
        try
        { image = ChdCdImage.Open(file, leaveOpen: true); }
        catch (ChdException failure) when (failure.Error != ChdError.None) { return failure.Error.ToString(); }
        catch (NotSupportedException) { return "unsupported"; }
        using (image)
            return $"frames-failed={Frames(file, image)} sectors-failed={Sectors(image)}";
    }

    private static int Frames(ChdFile file, ChdCdImage image)
    {
        ulong framesPerHunk = file.Header.HunkBytes / ChdCdImage.FrameBytes;
        byte[] hunk = new byte[file.Header.HunkBytes];
        byte[] frame = new byte[ChdCdImage.FrameBytes];
        bool hunkReads = false;
        int failed = 0;
        for (ulong index = 0; index < image.FrameCount; index++)
        {
            if (index % framesPerHunk == 0)
                hunkReads = Succeeds(() => file.ReadHunk((uint)(index / framesPerHunk), hunk));
            bool frameReads = Succeeds(() => image.ReadFrame(index, frame));
            if (frameReads != hunkReads)
                throw new CorpusFailure($"divergent frame {index}: ReadFrame {(frameReads ? "succeeds" : "fails")} where its hunk {(hunkReads ? "reads" : "fails")}");
            if (!frameReads)
            { failed++; continue; }
            int at = (int)(index % framesPerHunk) * ChdCdImage.FrameBytes;
            if (!frame.AsSpan().SequenceEqual(hunk.AsSpan(at, ChdCdImage.FrameBytes)))
                throw new CorpusFailure($"divergent frame {index}: its bytes differ from its hunk");
        }
        return failed;
    }

    private static int Sectors(ChdCdImage image)
    {
        byte[] sector = new byte[2352];
        int failed = 0;
        foreach (ChdCdTrack track in image.Tracks)
            for (uint frame = 0; frame < track.Frames; frame++)
                foreach (ChdCdSectorFormat format in Enum.GetValues<ChdCdSectorFormat>())
                {
                    try
                    { image.ReadSector(track.Number, frame, format, sector); }
                    catch (ChdException failure) when (failure.Error != ChdError.None) { failed++; }
                    catch (NotSupportedException) { /* The format is not stored for this track type. */ }
                }
        return failed;
    }

    private static string Integrity(ChdFile file)
    {
        try
        { return ChdIntegrity.Verify(file).IsVerified ? "verified" : "mismatch"; }
        catch (ChdException failure) when (failure.Error != ChdError.None) { return failure.Error.ToString(); }
    }

    private sealed record Decoded(ChdError? Rejected, uint[] Indices, string[] Hunks, uint HunkBytes, string Summary);

    /// <summary>The last hunk that decoded, kept to check that a later failing hunk leaves the hunk cache intact.</summary>
    private sealed class GoodHunk(int hunkBytes)
    {
        private readonly byte[] _bytes = new byte[hunkBytes];
        internal uint? Index { get; private set; }
        internal ReadOnlySpan<byte> Bytes => _bytes;

        internal void Remember(uint index, ReadOnlySpan<byte> bytes)
        {
            bytes.CopyTo(_bytes);
            Index = index;
        }
    }

    /// <summary>Two hunk destinations, each followed by a canary redzone.</summary>
    private sealed class HunkBuffers(int hunkBytes)
    {
        private readonly byte[] _first = new byte[hunkBytes + RedzoneBytes];
        private readonly byte[] _second = new byte[hunkBytes + RedzoneBytes];
        internal Span<byte> FirstMemory => _first;
        internal Span<byte> SecondMemory => _second;
        internal ReadOnlySpan<byte> First => _first.AsSpan(0, hunkBytes);
        internal ReadOnlySpan<byte> Second => _second.AsSpan(0, hunkBytes);
        internal bool Intact => _first.AsSpan(hunkBytes).IndexOfAnyExcept(Canary) < 0 && _second.AsSpan(hunkBytes).IndexOfAnyExcept(Canary) < 0;
    }
}
