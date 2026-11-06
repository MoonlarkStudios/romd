using System.Text.Json;
using System.Text.Json.Serialization;

namespace Moonlark.Libchdr.Benchmarks;

internal sealed record CaseMeasurement(string Fixture, string Operation, string Method, double MeanNanoseconds,
    double Confidence95HalfWidthNanoseconds, double? AllocatedBytesPerOperation, int Launches);

/// <summary>The measured case matrix and the strict JSON of its report. Measurements are recorded, never judged.</summary>
internal static class BenchmarkMatrix
{
    internal static readonly string[] Fixtures = ["dvd-lzma", "dvd-zlib", "dvd-huff", "dvd-flac", "dvd-zstd", "dvd-none", "cd-cdlz"];
    internal static readonly string[] Operations = ["Hunk", "Hit", "Miss", "Cross"];
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, RespectRequiredConstructorParameters = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
}
