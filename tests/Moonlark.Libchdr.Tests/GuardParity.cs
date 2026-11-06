using System.Globalization;
using Moonlark.Libchdr.Internal;

namespace Moonlark.Libchdr.Tests;

/// <summary>Where the libchdr fuzz harness (native/libchdr/fuzz/chd_walk_fuzzer.c) stopped an input.</summary>
internal enum HarnessStage
{
    /// <summary>Its mirror of ChdHeaderReader.ValidateEnvelope and ChdMetadataIndex.Read rejected the input before chd_open.</summary>
    Envelope,
    /// <summary>A harness-only bound (more than 1 MiB per hunk or 2,621,440 hunks) rejected the input before chd_open.</summary>
    Bound,
    /// <summary>chd_open failed.</summary>
    Open,
    /// <summary>Its mirror of ChdHeaderReader.Snapshot and ChdMapValidator rejected the opened input.</summary>
    Validation,
    /// <summary>The input passed every pre-read check and reached chd_read.</summary>
    Accept,
}

/// <summary>One disposition line the harness prints with CHDR_FUZZ_VERBOSE=1. Hunk geometry is set for a bound rejection
/// (the raw header geometry the bound refused) and an acceptance; the sampled read counts only for an acceptance.</summary>
internal sealed record HarnessDisposition(HarnessStage Stage, long Size, ulong HunkBytes, ulong HunkCount, uint Read, uint Decoded, string Reason)
{
    private const string ReasonField = " reason=";

    /// <summary>Parses the text after "chd_walk: ": "reject stage=&lt;stage&gt; size=&lt;n&gt; ... reason=&lt;text&gt;" or
    /// "accept size=&lt;n&gt; ... hunkbytes=&lt;n&gt; hunks=&lt;n&gt; ... read=&lt;n&gt; decoded=&lt;n&gt;".</summary>
    internal static HarnessDisposition Parse(string text)
    {
        int reasonAt = text.IndexOf(ReasonField, StringComparison.Ordinal);
        string reason = reasonAt < 0 ? "" : text[(reasonAt + ReasonField.Length)..];
        string[] words = (reasonAt < 0 ? text : text[..reasonAt]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string word in words.Skip(1))
        {
            int equals = word.IndexOf('=');
            if (equals > 0 && !fields.TryAdd(word[..equals], word[(equals + 1)..]))
                throw new InvalidDataException($"the harness disposition repeats {word[..equals]}: {text}");
        }
        HarnessStage stage = (words.FirstOrDefault(), fields.GetValueOrDefault("stage")) switch
        {
            ("accept", null) => HarnessStage.Accept,
            ("reject", "envelope") => HarnessStage.Envelope,
            ("reject", "bound") => HarnessStage.Bound,
            ("reject", "open") => HarnessStage.Open,
            ("reject", "validation") => HarnessStage.Validation,
            _ => throw new InvalidDataException($"unrecognized harness disposition: {text}"),
        };
        bool geometry = stage is HarnessStage.Bound or HarnessStage.Accept;
        bool walked = stage == HarnessStage.Accept;
        return new(stage, checked((long)Number(fields, "size")), geometry ? Number(fields, "hunkbytes") : 0,
            geometry ? Number(fields, "hunks") : 0, walked ? checked((uint)Number(fields, "read")) : 0,
            walked ? checked((uint)Number(fields, "decoded")) : 0, reason);
    }

    private static ulong Number(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out string? text) && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value)
            ? value
            : throw new InvalidDataException($"the harness disposition has no numeric {key}");
}

/// <summary>Where Moonlark.Libchdr's open path stopped an input.</summary>
internal enum ManagedStage
{
    /// <summary>ChdHeaderReader.ValidateEnvelope, which ChdFile runs before chd_open, rejected it.</summary>
    RejectedAtEnvelope,
    /// <summary>chd_open, ChdHeaderReader.Snapshot or ChdMapValidator rejected it, after the envelope check passed.</summary>
    RejectedAfterEnvelope,
    /// <summary>ChdFile.TryOpen opened it, so its hunks are read through chd_read.</summary>
    Admitted,
}

/// <summary>Moonlark.Libchdr's disposition of one input; the header is set only when admitted.</summary>
internal readonly record struct ManagedDisposition(ManagedStage Stage, ChdError Error, ChdHeader Header);

/// <summary>Checks that the fuzz harness's mirrored pre-read checks and Moonlark.Libchdr's own open path give every
/// deterministic seed the same disposition, and that the seeds include every DVD codec and recipe geometry the fuzz gate
/// requires. <c>scripts/native/fuzz.sh parity</c> runs it on the harness's replay log; the test runner never does.</summary>
/// <remarks>
/// Both sides read the whole seed: the parity replay runs without <c>-max_len</c>, so an input the harness saw truncated is
/// a mismatch. A rejection before chd_open must be one on both sides, as must an admission to chd_read; a rejection by
/// chd_open or the post-open checks matches any rejection after Moonlark.Libchdr's envelope check, since both run the same
/// libchdr. The harness-only bounds have no Moonlark.Libchdr counterpart: they equal ROMD's CHD preflight limits
/// (<c>ChdDvdLimits</c>), which refuse such files before ChdFile opens them. A bound rejection therefore agrees only when
/// the geometry it reports exceeds one of those limits, and then with a rejection after the envelope check, or with an
/// admission whose header has exactly that geometry.
/// The read-ahead budget never changes admission: chd_set_cache_budget fails only for a null handle or allocation failure,
/// which ChdFile tolerates, so the managed side opens with read-ahead off.
/// </remarks>
internal static class GuardParity
{
    internal const string RunningPrefix = "Running: ";
    internal const string DispositionPrefix = "chd_walk: ";

    /// <summary>The harness's MAX_HUNK_BYTES and MAX_HUNK_COUNT, equal to ROMD's <c>ChdDvdLimits</c> (a Romd.Media.Tests
    /// test ties the harness to those limits); a bound rejection agrees only with a geometry beyond one of them.</summary>
    internal const ulong HarnessMaxHunkBytes = 1 << 20;
    internal const ulong HarnessMaxHunkCount = 2_621_440;

    /// <summary>The agreeing outcomes, in report order; <see cref="Agreement"/> returns an index into this list.</summary>
    internal static readonly string[] Buckets =
    [
        "envelope    both reject before chd_open",
        "open        chd_open fails in the harness; Moonlark.Libchdr rejects after its envelope check",
        "validation  the harness's map or geometry check rejects; Moonlark.Libchdr rejects after its envelope check",
        "accept      both admit the input to chd_read",
        "bound       a harness-only bound rejects; Moonlark.Libchdr rejects after its envelope check",
        "bound       a harness-only bound rejects; Moonlark.Libchdr admits that over-bound geometry, which ROMD's CHD preflight refuses",
    ];

    /// <summary>The parent-free v5 DVD seeds the fuzz gate requires: every supported DVD codec at chdman's 16 KiB DVD hunk,
    /// which includes ROMD's current recipe (zlib) and one candidate (zstd), and the other candidate, zstd at 4 KiB.</summary>
    internal static readonly (ChdCodec Codec, uint HunkBytes)[] RequiredDvdSeeds =
    [
        (ChdCodec.None, 16384), (ChdCodec.Zlib, 16384), (ChdCodec.Lzma, 16384), (ChdCodec.Huffman, 16384),
        (ChdCodec.Flac, 16384), (ChdCodec.Zstd, 16384), (ChdCodec.Zstd, 4096),
    ];

    private static readonly string[] SeedSets = ["fixtures", "malformed"];

    /// <summary>Compares the harness's replay log with Moonlark.Libchdr's dispositions of every seed under
    /// <paramref name="seedsDirectory"/> (its fixtures and malformed sets) and writes the counts; returns 0 only with no
    /// mismatch and every required codec seed present.</summary>
    internal static int Run(string harnessLog, string seedsDirectory, TextWriter output)
    {
        _ = NativeTestEnvironment.Root; // loads the digest-verified host libchdr build
        Dictionary<string, HarnessDisposition> harness = ParseHarnessLog(File.ReadAllLines(harnessLog));
        string root = Path.GetFullPath(seedsDirectory);
        List<string> inputs = [.. SeedSets.SelectMany(set => Directory.EnumerateFiles(Path.Combine(root, set), "*.chd")
            .Select(path => set + "/" + Path.GetFileName(path))).Order(StringComparer.Ordinal)];
        int[] counts = new int[Buckets.Length];
        var mismatches = new List<string>();
        var fixtures = new List<(string Input, ChdHeader Header, HarnessDisposition Harness)>();
        foreach (string input in inputs)
        {
            string path = Path.Combine(root, input);
            ManagedDisposition managed = Classify(path);
            HarnessDisposition? reported = harness.GetValueOrDefault(input);
            if (Compare(reported, managed, new FileInfo(path).Length) is { } mismatch)
            {
                mismatches.Add($"{input}: {mismatch}");
                continue;
            }
            counts[Agreement(reported!, managed)]++;
            if (managed.Stage == ManagedStage.Admitted && input.StartsWith("fixtures/", StringComparison.Ordinal))
                fixtures.Add((input, managed.Header, reported!));
        }
        if (inputs.Count == 0)
            mismatches.Add($"no seeds under {root}");
        mismatches.AddRange(harness.Keys.Except(inputs, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(input => $"{input}: the harness replayed an input that is not a seed"));
        output.WriteLine($"guard parity: {inputs.Count} seeds, {mismatches.Count} mismatches");
        for (int bucket = 0; bucket < Buckets.Length; bucket++)
            output.WriteLine($"  {counts[bucket],6}  {Buckets[bucket]}");
        foreach (string mismatch in mismatches)
            output.WriteLine($"  mismatch: {mismatch}");
        int missing = ReportCodecSeeds(fixtures, output);
        return mismatches.Count == 0 && missing == 0 ? 0 : 1;
    }

    /// <summary>Pairs each disposition line with the input libFuzzer announced before it ("Running: &lt;path&gt;").</summary>
    internal static Dictionary<string, HarnessDisposition> ParseHarnessLog(string[] lines)
    {
        var dispositions = new Dictionary<string, HarnessDisposition>(StringComparer.Ordinal);
        string? input = null;
        foreach (string line in lines)
        {
            if (line.StartsWith(RunningPrefix, StringComparison.Ordinal))
                input = line[RunningPrefix.Length..];
            else if (line.StartsWith(DispositionPrefix, StringComparison.Ordinal))
            {
                if (input is null)
                    throw new InvalidDataException($"a harness disposition follows no input: {line}");
                if (!dispositions.TryAdd(input, HarnessDisposition.Parse(line[DispositionPrefix.Length..])))
                    throw new InvalidDataException($"the harness reported {input} twice");
                input = null;
            }
        }
        return dispositions;
    }

    /// <summary>Opens the file through ChdFile.TryOpen, Moonlark.Libchdr's pre-read admission, and separately runs
    /// ChdHeaderReader.ValidateEnvelope, which TryOpen applies before chd_open, to place a rejection before or after it.</summary>
    internal static ManagedDisposition Classify(string path)
    {
        ChdError? envelope = null;
        using (var context = new ChdSourceContext(new FileChdDataSource(path), leaveOpen: false))
        {
            try
            { _ = ChdHeaderReader.ValidateEnvelope(context); }
            catch (ChdValidationException error) { envelope = error.Error; }
        }
        if (ChdFile.TryOpen(path, out ChdFile? file, out ChdError failure))
        {
            using (file)
            {
                if (envelope is { } error)
                    throw new InvalidOperationException($"ChdFile.TryOpen admits {path}, which ChdHeaderReader.ValidateEnvelope rejects " +
                        $"({error}); the parity check no longer follows ChdFile's open path.");
                return new(ManagedStage.Admitted, ChdError.None, file.Header);
            }
        }
        return envelope is { } rejected
            ? new(ManagedStage.RejectedAtEnvelope, rejected, default)
            : new(ManagedStage.RejectedAfterEnvelope, failure, default);
    }

    /// <summary>Why the two dispositions of a <paramref name="fileBytes"/>-byte seed disagree, or null when they agree.</summary>
    internal static string? Compare(HarnessDisposition? harness, ManagedDisposition managed, long fileBytes)
    {
        if (harness is null)
            return "the harness reported no disposition";
        if (harness.Size != fileBytes)
            return $"the harness saw {harness.Size} of its {fileBytes} bytes";
        if (harness.Stage == HarnessStage.Bound && harness.HunkBytes <= HarnessMaxHunkBytes && harness.HunkCount <= HarnessMaxHunkCount)
            return $"the harness bounds a geometry within its limits (hunkbytes={harness.HunkBytes} hunks={harness.HunkCount})";
        bool agrees = (harness.Stage, managed.Stage) switch
        {
            (HarnessStage.Envelope, ManagedStage.RejectedAtEnvelope) => true,
            (HarnessStage.Open or HarnessStage.Validation or HarnessStage.Bound, ManagedStage.RejectedAfterEnvelope) => true,
            (HarnessStage.Accept, ManagedStage.Admitted) => true,
            (HarnessStage.Bound, ManagedStage.Admitted) =>
                managed.Header.HunkBytes == harness.HunkBytes && managed.Header.HunkCount == harness.HunkCount,
            _ => false,
        };
        return agrees ? null : $"the harness {Describe(harness)}, but Moonlark.Libchdr {Describe(managed)}";
    }

    /// <summary>The index in <see cref="Buckets"/> of two dispositions that <see cref="Compare"/> found in agreement.</summary>
    internal static int Agreement(HarnessDisposition harness, ManagedDisposition managed) => harness.Stage switch
    {
        HarnessStage.Envelope => 0,
        HarnessStage.Open => 1,
        HarnessStage.Validation => 2,
        HarnessStage.Accept => 3,
        _ => managed.Stage == ManagedStage.Admitted ? 5 : 4,
    };

    /// <summary>Whether a header is a parent-free v5 DVD image with 2,048-byte units, <paramref name="codec"/> in slot 0, the
    /// other slots empty, and <paramref name="hunkBytes"/>-byte hunks.</summary>
    internal static bool IsDvdSeed(ChdHeader header, ChdCodec codec, uint hunkBytes) =>
        header.Version == 5 && !header.HasParent && header.UnitBytes == 2048 && header.HunkBytes == hunkBytes &&
        header.GetCodec(0) == codec && header.GetCodec(1) == ChdCodec.None && header.GetCodec(2) == ChdCodec.None &&
        header.GetCodec(3) == ChdCodec.None;

    /// <summary>The first admitted seed with the codec and geometry whose every sampled hunk the harness decoded, described
    /// for the report, or null.</summary>
    internal static string? FindCodecSeed(List<(string Input, ChdHeader Header, HarnessDisposition Harness)> admitted, ChdCodec codec,
        uint hunkBytes) =>
        admitted.Where(seed => IsDvdSeed(seed.Header, codec, hunkBytes) && seed.Harness.Read > 0 && seed.Harness.Decoded == seed.Harness.Read)
            .Select(seed => $"{seed.Input} (the harness decoded all {seed.Harness.Read} sampled hunks)")
            .FirstOrDefault();

    private static int ReportCodecSeeds(List<(string Input, ChdHeader Header, HarnessDisposition Harness)> fixtures, TextWriter output)
    {
        output.WriteLine("codec seeds: parent-free v5 DVD fixtures, 2,048-byte units, one codec slot");
        int missing = 0;
        foreach ((ChdCodec codec, uint hunkBytes) in RequiredDvdSeeds)
        {
            string? seed = FindCodecSeed(fixtures, codec, hunkBytes);
            output.WriteLine($"  {codec,-8} {hunkBytes,5}-byte hunks  {seed ?? "MISSING"}");
            if (seed is null)
                missing++;
        }
        return missing;
    }

    private static string Describe(HarnessDisposition harness) => harness.Stage switch
    {
        HarnessStage.Envelope => $"rejects it before chd_open ({harness.Reason})",
        HarnessStage.Bound => $"bounds it ({harness.Reason}; hunkbytes={harness.HunkBytes} hunks={harness.HunkCount})",
        HarnessStage.Open => $"fails chd_open ({harness.Reason})",
        HarnessStage.Validation => $"rejects it after chd_open ({harness.Reason})",
        _ => "admits it to chd_read",
    };

    private static string Describe(ManagedDisposition managed) => managed.Stage switch
    {
        ManagedStage.RejectedAtEnvelope => $"rejects it in ValidateEnvelope ({managed.Error})",
        ManagedStage.RejectedAfterEnvelope => $"rejects it after ValidateEnvelope ({managed.Error})",
        _ => $"admits it (hunkbytes={managed.Header.HunkBytes} hunks={managed.Header.HunkCount})",
    };
}
