using System.Text.Json;

namespace Romd.ScaleHarness;

/// <summary>
///     Written to the data directory after generation so subsequent runs can safely reuse the
///     dataset (and scenarios can locate deterministic anchors like the capped job id). The
///     database name is recorded so a manifest never vouches for a different database.
/// </summary>
public sealed record DatasetManifest(
    int SpecVersion,
    string Scale,
    ulong Seed,
    string DatabaseName,
    DateTimeOffset GeneratedAt,
    double GenerationSeconds,
    Guid BigJobId,
    int LargestPlatformId,
    string SearchToken,
    int ExpectedSagaTitleCount,
    Dictionary<string, long> RowCounts,
    List<string> SearchChecks)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task WriteAsync(string path, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, JsonOptions, cancellationToken);
    }

    public static async Task<DatasetManifest?> TryReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<DatasetManifest>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public bool Matches(ScaleParameters scale, string databaseName) =>
        SpecVersion == ScaleParameters.SpecVersion
        && Scale == scale.Name
        && Seed == ScaleParameters.Seed
        && DatabaseName == databaseName;
}
