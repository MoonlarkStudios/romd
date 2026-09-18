namespace Romd.ScaleHarness;

/// <summary>
///     Dataset spec v4 scenario parameter sets. The driving axis is Titles; every other
///     population is derived from the versioned ratios in the issue #98 design comment:
///     CatalogReleases ~ 1.2x Titles, CatalogReleaseFiles mirror the representative game's files,
///     SourceEntries and TitleSourceLinks = DatGames, MaterializedLibraryTitles = Titles x 2 libraries,
///     MaterializedLibraryReleases = DatGames x 2 libraries, JobItems ~ 1 per DatRom across a
///     handful of synthetic jobs, and partial enrichment layers on ~40% of titles.
/// </summary>
public sealed record ScaleParameters(string Name, int DatGames, int Titles, int DatRoms)
{
    public const int SpecVersion = 4;
    public const ulong Seed = 0x524F4D445F763421UL; // "ROMD_v4!"

    public const int LibraryCount = 2;
    public const int JobCount = 5;
    public const int RegionCount = 10;
    public const int LanguageCount = 10;
    public const int GamesPerDatFile = 5000;
    public const int SentinelRowsPerFtsTable = 200;

    public static readonly ScaleParameters Scale100K = new("100k", 120_000, 100_000, 300_000);
    public static readonly ScaleParameters Scale1M = new("1m", 1_200_000, 1_000_000, 3_000_000);

    /// <summary>Per-platform share (percent) of both titles and dat games; must sum to 100.</summary>
    public static readonly int[] PlatformWeights = [40, 20, 10, 8, 7, 6, 5, 4];

    public static int PlatformCount => PlatformWeights.Length;

    public int CatalogReleases => Titles * 12 / 10;
    public int SourceEntries => DatGames;
    public int TitleSourceLinks => DatGames;
    public int MaterializedLibraryTitles => Titles * LibraryCount;
    public int MaterializedLibraryReleases => DatGames * LibraryCount;
    public int JobItems => DatRoms;
    public int EnrichedTitles => Titles * 2 / 5;

    /// <summary>The first synthetic job absorbs 40% of all job items (100k exactly at 100k scale).</summary>
    public int FirstJobItemCount => JobItems * 2 / 5;

    public static ScaleParameters Parse(string value) => value.ToLowerInvariant() switch
    {
        "100k" => Scale100K,
        "1m" => Scale1M,
        _ => throw new ArgumentException($"Unknown scale '{value}'. Expected '100k' or '1m'.")
    };
}
