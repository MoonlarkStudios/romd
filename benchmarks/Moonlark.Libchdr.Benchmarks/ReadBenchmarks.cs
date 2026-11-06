using System.Security.Cryptography;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Moonlark.Libchdr.Internal;

namespace Moonlark.Libchdr.Benchmarks;

/// <summary>Paired native and safe reads over identical synthetic files, callback sources, buffers and zero read-ahead budgets.</summary>
[MemoryDiagnoser]
public class ReadBenchmarks : IDisposable
{
    private RawChd _raw = null!;
    private ChdFile _safe = null!;
    private byte[] _rawOutput = null!;
    private byte[] _safeOutput = null!;
    private int _hunkBytes;
    private uint _rawSequence;
    private uint _safeSequence;

    /// <summary>The six DVD codecs and one CD codec; fixture setup is outside timing.</summary>
    [Params("dvd-lzma", "dvd-zlib", "dvd-huff", "dvd-flac", "dvd-zstd", "dvd-none", "cd-cdlz")]
    public string Fixture { get; set; } = "dvd-lzma";

    /// <summary>Whole hunk decode, one-hunk cache hit, alternating cache miss or a two-hunk range.</summary>
    [Params("Hunk", "Hit", "Miss", "Cross")]
    public string Operation { get; set; } = "Hunk";

    /// <summary>Verifies input identities, opens paired handles, warms them and checks equality and exact thread allocation before timing.</summary>
    [GlobalSetup]
    public void Setup()
    {
        string root = Environment.GetEnvironmentVariable("MOONLARK_BENCHMARK_ROOT")
            ?? throw new InvalidOperationException("MOONLARK_BENCHMARK_ROOT must identify the verified source checkout.");
        string inputs = Environment.GetEnvironmentVariable("MOONLARK_BENCHMARK_INPUTS")
            ?? throw new InvalidOperationException("The benchmark run input receipt is missing.");
        string parentHash = Environment.GetEnvironmentVariable("MOONLARK_BENCHMARK_INPUTS_SHA256")
            ?? throw new InvalidOperationException("The original parent receipt identity is missing.");
        string childHash = Environment.GetEnvironmentVariable(ChildBuildToolchain.ChildHashVariable)
            ?? throw new InvalidOperationException("The parent-issued child receipt identity is missing.");
        System.Reflection.Assembly childEntry = System.Reflection.Assembly.GetEntryAssembly()
            ?? throw new InvalidOperationException("The generated child entry assembly is missing.");
        ChildBuildInputs.VerifyChild(root, inputs, parentHash, Path.GetDirectoryName(childEntry.Location)!, childEntry.GetName().Name!,
            typeof(ReadBenchmarks).Assembly.Location, typeof(ChdFile).Assembly.Location, childHash);
        (string rid, string filename) = NativePlatform.Current();
        string nativeDirectory = Path.Combine(root, "artifacts", "native", "libchdr", rid);
        using JsonDocument nativeManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(nativeDirectory, "build-manifest.json")));
        string native = Path.Combine(nativeDirectory, "native", filename);
        VerifyFile(native, nativeManifest.RootElement.GetProperty("size").GetInt64(), nativeManifest.RootElement.GetProperty("sha256").GetString()!);
        string fixtures = Path.Combine(root, "artifacts", "fixtures", "libchdr-final");
        using JsonDocument fixtureManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, "fixtures-manifest.json")));
        JsonElement entry = fixtureManifest.RootElement.GetProperty("fixtures").EnumerateArray()
            .Single(item => item.GetProperty("chd").GetProperty("path").GetString() == Fixture + ".chd").GetProperty("chd");
        string path = Path.Combine(fixtures, Fixture + ".chd");
        VerifyFile(path, entry.GetProperty("bytes").GetInt64(), entry.GetProperty("sha256").GetString()!);
        LibchdrLibrary.Load(native);
        _raw = new RawChd(path);
        try
        {
            _safe = ChdFile.Open(path, new ChdOpenOptions { ReadAheadBytes = 0 });
            _hunkBytes = checked((int)_safe.Header.HunkBytes);
            if (_raw.HunkBytes != _hunkBytes || _raw.LogicalBytes != _safe.Header.LogicalBytes || _safe.ReadAheadBytes != 0
                || _safe.Header.HunkCount < 4 || _hunkBytes < 8192)
                throw new InvalidDataException("Paired geometry or budgets disagree.");
            _rawOutput = new byte[_hunkBytes];
            _safeOutput = new byte[_hunkBytes];
            for (int index = 0; index < 32; index++)
            {
                _ = Raw();
                _ = Safe();
                if (!_rawOutput.AsSpan().SequenceEqual(_safeOutput))
                    throw new InvalidDataException("Raw and safe outputs disagree.");
            }
            // Prime both one-hunk caches identically. Whole-hunk reads bypass each cache.
            _raw.ReadAt(_hunkBytes + 17L, _rawOutput.AsSpan(0, 4096));
            _safe.ReadAt(_hunkBytes + 17L, _safeOutput.AsSpan(0, 4096));
            _rawSequence = _safeSequence = 0;
            for (int index = 0; index < 1024; index++)
            { _ = Raw(); _ = Safe(); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 1024; index++)
            { _ = Raw(); _ = Safe(); }
            if (GC.GetAllocatedBytesForCurrentThread() != before)
                throw new InvalidDataException("Steady-state preflight allocated managed bytes.");
            _rawSequence = _safeSequence = 0;
        }
        catch { Cleanup(); throw; }
    }

    /// <summary>Direct generated chd_read for Hunk; benchmark-only native one-hunk-cache adapter for range operations.</summary>
    [Benchmark(Baseline = true)]
    public byte Raw()
    {
        if (Operation == "Hunk")
            _raw.ReadHunk(1 + (_rawSequence++ & 1), _rawOutput);
        else
            _raw.ReadAt(Offset(ref _rawSequence), _rawOutput.AsSpan(0, 4096));
        return _rawOutput[23];
    }

    /// <summary>Safe ReadHunk or ReadAt over the same deterministic access sequence and output size.</summary>
    [Benchmark]
    public byte Safe()
    {
        if (Operation == "Hunk")
            _safe.ReadHunk(1 + (_safeSequence++ & 1), _safeOutput);
        else
            _safe.ReadAt(Offset(ref _safeSequence), _safeOutput.AsSpan(0, 4096));
        return _safeOutput[23];
    }

    private long Offset(ref uint sequence) => Operation switch
    {
        "Hit" => _hunkBytes + 17L,
        "Miss" => (1L + (sequence++ & 1)) * _hunkBytes + 17,
        "Cross" => 2L * _hunkBytes - 2048,
        _ => throw new InvalidOperationException("Unknown operation"),
    };

    /// <summary>Releases each decoder and its source after the measured process finishes.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <summary>Releases both benchmark handles and callback sources.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Supports disposal of the generated benchmark subclass.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
            return;
        _safe?.Dispose();
        _raw?.Dispose();
    }

    private static void VerifyFile(string path, long bytes, string sha256)
    {
        byte[] contents = File.ReadAllBytes(path);
        if (contents.LongLength != bytes || Convert.ToHexStringLower(SHA256.HashData(contents)) != sha256)
            throw new InvalidDataException("Benchmark input differs from its receipt: " + path);
    }
}
