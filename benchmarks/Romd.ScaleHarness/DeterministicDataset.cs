using Romd.Domain.Libraries;

namespace Romd.ScaleHarness;

/// <summary>
///     Pure deterministic value model for dataset spec v3. Every row value is a function of the
///     spec seed and a row index (splitmix64 finalizer, no dependence on <see cref="Random" /> or
///     framework hash codes), so the generator and the scenarios can independently recompute
///     identical rows across runs and .NET versions.
/// </summary>
public sealed class DeterministicDataset
{
    private const ulong PurposeSha1 = 0x53484131UL;
    private const ulong PurposeName = 0x4E414D45UL;
    private const ulong PurposeMeta = 0x4D455441UL;
    private const ulong PurposeGuid = 0x47554944UL;

    private static readonly string[] Adjectives =
    [
        "Super", "Mega", "Hyper", "Turbo", "Cosmic", "Shadow", "Golden", "Crystal",
        "Iron", "Neon", "Solar", "Lunar", "Mystic", "Rogue", "Silent", "Crimson",
        "Frozen", "Blazing", "Ancient", "Final", "Lost", "Secret", "Wild", "Brave",
        "Dark", "Bright", "Savage", "Noble", "Phantom", "Radiant", "Stellar", "Storm"
    ];

    private static readonly string[] Nouns =
    [
        "Dragon", "Knight", "Quest", "Racer", "Fighter", "Fortress", "Galaxy", "Legend",
        "Warrior", "Wizard", "Kingdom", "Raider", "Pilot", "Hunter", "Samurai", "Ninja",
        "Castle", "Island", "Cavern", "Arena", "Circuit", "Empire", "Voyage", "Rebellion",
        "Odyssey", "Frontier", "Dungeon", "Tempest", "Vanguard", "Horizon", "Machine", "Falcon"
    ];

    private static readonly string[] Genres =
    [
        "Platformer", "RPG", "Racing", "Shooter", "Puzzle", "Fighting",
        "Adventure", "Sports", "Strategy", "Simulation", "Beat-em-up", "Rhythm"
    ];

    private static readonly string[] Publishers =
    [
        "Otterworks", "Pixel Forge", "Cartridge Co", "Bitstream", "RetroSoft", "Polyplay",
        "Neon Circuit", "Kelp Games", "Riverbed", "Arcadia", "Northlight", "Waveform",
        "Redwood Interactive", "Blue Shell", "Copperline", "Starcade", "Driftwood", "Lucent",
        "Halcyon", "Emberworks"
    ];

    private static readonly string[] RegionNames =
    [
        "USA", "Europe", "Japan", "World", "Australia", "Brazil", "Korea", "China", "France", "Germany"
    ];

    private static readonly string[] LanguageNames =
    [
        "English", "French", "German", "Spanish", "Italian", "Japanese", "Portuguese", "Dutch",
        "Korean", "Chinese"
    ];

    private static readonly string[] RatingCodes = ["E", "E10", "T", "M"];
    private static readonly int[] RatingAges = [6, 10, 13, 17];

    public static readonly Guid SystemUserId = new("00000000-0000-0000-0000-000000000001");
    public static readonly DateTimeOffset BaseTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Token guaranteed to appear (as the standalone word "Saga") in 70% of title names.</summary>
    public const string SearchToken = "saga";

    private readonly int[] _titleStart;
    private readonly int[] _titleCount;
    private readonly int[] _gameStart;
    private readonly int[] _gameCount;
    private readonly int[] _datFileStart;
    private readonly int[] _datFileCount;
    private readonly int[] _catalogReleaseByGame;
    private readonly int[] _titleByCatalogRelease;
    private readonly int[] _releaseSourceCount;
    private readonly int[] _representativeGameByRelease;
    private readonly int[] _releaseFileStart;
    private readonly int[] _releaseFileCount;
    private readonly int[] _romFileIdByCatalogFile;
    private readonly long[] _sourceRomByRomFile;

    public DeterministicDataset(ScaleParameters scale)
    {
        Scale = scale;
        int platforms = ScaleParameters.PlatformCount;
        _titleStart = new int[platforms];
        _titleCount = new int[platforms];
        _gameStart = new int[platforms];
        _gameCount = new int[platforms];
        _datFileStart = new int[platforms];
        _datFileCount = new int[platforms];

        int cumWeight = 0, titleCursor = 0, gameCursor = 0, datFileCursor = 0;
        for (int p = 0; p < platforms; p++)
        {
            int nextWeight = cumWeight + ScaleParameters.PlatformWeights[p];
            _titleStart[p] = titleCursor;
            _titleCount[p] = (int)((long)scale.Titles * nextWeight / 100) - titleCursor;
            _gameStart[p] = gameCursor;
            _gameCount[p] = (int)((long)scale.DatGames * nextWeight / 100) - gameCursor;
            _datFileStart[p] = datFileCursor;
            _datFileCount[p] = (_gameCount[p] + ScaleParameters.GamesPerDatFile - 1) / ScaleParameters.GamesPerDatFile;

            titleCursor += _titleCount[p];
            gameCursor += _gameCount[p];
            datFileCursor += _datFileCount[p];
            cumWeight = nextWeight;
        }

        DatFileCount = datFileCursor;

        _catalogReleaseByGame = new int[scale.DatGames];
        _titleByCatalogRelease = new int[scale.CatalogReleases];
        for (int title = 0; title < scale.Titles; title++)
        {
            _titleByCatalogRelease[title] = title;
        }
        _releaseSourceCount = new int[scale.CatalogReleases];
        _representativeGameByRelease = Enumerable.Repeat(-1, scale.CatalogReleases).ToArray();
        var secondReleaseByTitle = Enumerable.Repeat(-1, scale.Titles).ToArray();
        int secondReleaseCursor = scale.Titles;
        for (int p = 0; p < platforms; p++)
        {
            int titlesWithSecondRelease = _gameCount[p] - _titleCount[p];
            for (int offset = 0; offset < titlesWithSecondRelease; offset++)
            {
                int title = _titleStart[p] + offset;
                secondReleaseByTitle[title] = secondReleaseCursor;
                _titleByCatalogRelease[secondReleaseCursor++] = title;
            }
        }

        if (secondReleaseCursor != scale.CatalogReleases)
        {
            throw new InvalidOperationException(
                $"Synthetic release layout produced {secondReleaseCursor:N0} releases; expected {scale.CatalogReleases:N0}.");
        }

        for (int g = 0; g < scale.DatGames; g++)
        {
            int platformIndex = PlatformOfGame(g) - 1;
            int titleIndex = TitleOfGame(g);
            int occurrence = (g - _gameStart[platformIndex]) / _titleCount[platformIndex];
            int releaseIndex = secondReleaseByTitle[titleIndex] >= 0 && occurrence % 2 == 1
                ? secondReleaseByTitle[titleIndex]
                : titleIndex;

            _catalogReleaseByGame[g] = releaseIndex + 1;
            _releaseSourceCount[releaseIndex]++;
            if (_representativeGameByRelease[releaseIndex] < 0)
            {
                _representativeGameByRelease[releaseIndex] = g;
            }
        }

        _releaseFileStart = new int[scale.CatalogReleases];
        _releaseFileCount = new int[scale.CatalogReleases];
        int releaseFileCursor = 0;
        for (int r = 0; r < scale.CatalogReleases; r++)
        {
            int representativeGame = _representativeGameByRelease[r];
            if (representativeGame < 0)
            {
                throw new InvalidOperationException($"Synthetic release {r + 1} has no source game.");
            }

            (long start, long end) = RomRangeOfGame(representativeGame);
            _releaseFileStart[r] = releaseFileCursor;
            _releaseFileCount[r] = checked((int)(end - start));
            releaseFileCursor += _releaseFileCount[r];
        }

        CatalogReleaseFileCount = releaseFileCursor;

        _romFileIdByCatalogFile = new int[CatalogReleaseFileCount + 1];
        var sourceRoms = new List<long>();
        for (long j = 0; j < scale.DatRoms; j++)
        {
            if (!RomOwned(j))
            {
                continue;
            }

            int catalogFileId = CatalogReleaseFileOfRom(j);
            if (_romFileIdByCatalogFile[catalogFileId] == 0)
            {
                sourceRoms.Add(j);
                _romFileIdByCatalogFile[catalogFileId] = sourceRoms.Count;
            }
        }

        _sourceRomByRomFile = sourceRoms.ToArray();
        RomFileCount = sourceRoms.Count;
    }

    public ScaleParameters Scale { get; }
    public int DatFileCount { get; }
    public int RomFileCount { get; }
    public int CatalogReleaseFileCount { get; }

    /// <summary>Platform id 1 carries the largest share (40%) and is the matcher scale target.</summary>
    public int LargestPlatformId => 1;

    public int TitleCountForPlatform(int platformId) => _titleCount[platformId - 1];

    // ---- indices are 0-based throughout; database ids are index + 1 ----

    public int PlatformOfTitle(int t) => IndexToPlatform(_titleStart, _titleCount, t);
    public int PlatformOfGame(int g) => IndexToPlatform(_gameStart, _gameCount, g);

    public int TitleOfGame(int g)
    {
        int p = PlatformOfGame(g) - 1;
        return _titleStart[p] + (g - _gameStart[p]) % _titleCount[p];
    }

    public int DatFileOfGame(int g)
    {
        int p = PlatformOfGame(g) - 1;
        return _datFileStart[p] + (g - _gameStart[p]) / ScaleParameters.GamesPerDatFile;
    }

    public int PlatformOfDatFile(int d) => IndexToPlatform(_datFileStart, _datFileCount, d);

    public int GameCountForDatFile(int d)
    {
        int p = PlatformOfDatFile(d) - 1;
        int firstGame = _gameStart[p] + (d - _datFileStart[p]) * ScaleParameters.GamesPerDatFile;
        int platformEnd = _gameStart[p] + _gameCount[p];
        return Math.Min(ScaleParameters.GamesPerDatFile, platformEnd - firstGame);
    }

    public int RomCountForDatFile(int d)
    {
        int p = PlatformOfDatFile(d) - 1;
        int firstGame = _gameStart[p] + (d - _datFileStart[p]) * ScaleParameters.GamesPerDatFile;
        int gameCount = GameCountForDatFile(d);
        long start = RomRangeOfGame(firstGame).Start;
        long end = RomRangeOfGame(firstGame + gameCount - 1).End;
        return checked((int)(end - start));
    }

    public (long Start, long End) RomRangeOfGame(int g) =>
        ((long)Scale.DatRoms * g / Scale.DatGames, (long)Scale.DatRoms * (g + 1) / Scale.DatGames);

    public static bool RomOwned(long j) => j % 10 < 3;

    public int RomFileIdOfRom(long j) => _romFileIdByCatalogFile[CatalogReleaseFileOfRom(j)];

    public bool GameHasLocalPayload(int g)
    {
        var range = RomRangeOfGame(g);
        for (long rom = range.Start; rom < range.End; rom++)
        {
            if (RomFileIdOfRom(rom) > 0)
            {
                return true;
            }
        }

        return false;
    }

    public bool TitleHasLocalPayload(int t)
    {
        int platform = PlatformOfTitle(t) - 1;
        int offset = t - _titleStart[platform];
        int platformGameEnd = _gameStart[platform] + _gameCount[platform];
        for (int game = _gameStart[platform] + offset;
             game < platformGameEnd;
             game += _titleCount[platform])
        {
            if (GameHasLocalPayload(game))
            {
                return true;
            }
        }

        return false;
    }

    public long SourceRomOfRomFile(int romFileIndex) => _sourceRomByRomFile[romFileIndex];

    public byte[] RomSha1(long j) => CatalogReleaseFileSha1(CatalogReleaseFileOfRom(j) - 1);

    public byte[] RomMd5(long j) => RomSha1(j)[..16];

    public byte[] RomCrc(long j) => RomSha1(j)[..4];

    public long RomSize(long j) => CatalogReleaseFileSize(CatalogReleaseFileOfRom(j) - 1);

    public byte[] FileSha256(int fileIndex) => HashBytes(PurposeSha1 ^ 0xFFUL, (ulong)fileIndex, 32);

    // ---- titles ----

    public bool TitleEnriched(int t) => t % 5 < 2;
    public bool TitleTracked(int t) => t % 3 != 0;
    public bool TitleHasSaga(int t) => t % 10 < 7;

    public string TitleName(int t)
    {
        ulong h = Mix(PurposeName, (ulong)t);
        string adj = Adjectives[(int)(h % 32)];
        string noun = Nouns[(int)((h >> 8) % 32)];
        string saga = TitleHasSaga(t) ? " Saga" : "";
        return $"{adj} {noun}{saga} {t + 1:D7}";
    }

    public string? TitleDescription(int t) =>
        t % 10 < 3
            ? $"A {Genres[(int)(Mix(PurposeMeta, (ulong)t) % 12)].ToLowerInvariant()} journey across {Nouns[t % 32].ToLowerInvariant()} worlds."
            : null;

    public string? TitleGenre(int t) =>
        TitleEnriched(t) ? Genres[(int)(Mix(PurposeMeta, (ulong)t) % (ulong)Genres.Length)] : null;

    public string? TitlePublisher(int t) =>
        TitleEnriched(t) ? Publishers[(int)(Mix(PurposeMeta ^ 1, (ulong)t) % (ulong)Publishers.Length)] : null;

    public double? TitleRating(int t) =>
        TitleEnriched(t) ? 5.0 + (int)(Mix(PurposeMeta ^ 2, (ulong)t) % 50) / 10.0 : null;

    public DateOnly? TitleReleaseDate(int t)
    {
        if (!TitleEnriched(t))
        {
            return null;
        }

        ulong h = Mix(PurposeMeta ^ 3, (ulong)t);
        return new DateOnly(1980 + (int)(h % 45), 1 + (int)((h >> 8) % 12), 1 + (int)((h >> 16) % 28));
    }

    public int? TitlePlayers(int t) =>
        TitleEnriched(t) ? 1 + (int)(Mix(PurposeMeta ^ 4, (ulong)t) % 4) : null;

    /// <summary>Exact number of titles whose name contains the standalone "Saga" token.</summary>
    public int ExpectedSagaTitleCount() =>
        Enumerable.Range(0, Scale.Titles).Count(TitleHasSaga);

    // ---- dat games ----

    // The final parenthetical is a deterministic DAT-entry discriminator. Some smaller
    // platform populations wrap titles within one synthetic DAT file; keeping the raw names
    // unique models the source entry key while TitleNormalizer still resolves every variant
    // to its canonical TitleName.
    public string GameName(int g) =>
        $"{TitleName(TitleOfGame(g))} ({RegionNames[g % 10]}) (Set {g + 1:D7})";

    public string? GameDescription(int g) => g % 4 == 0 ? GameName(g) : null;

    public string? GameYear(int g) =>
        g % 3 == 0 ? (1980 + (int)(Mix(PurposeMeta ^ 5, (ulong)g) % 45)).ToString() : null;

    public string? GameManufacturer(int g) =>
        g % 3 == 0 ? Publishers[(int)(Mix(PurposeMeta ^ 6, (ulong)g) % (ulong)Publishers.Length)] : null;

    public int RegionIdOfGame(int g) => g % ScaleParameters.RegionCount + 1;
    public int LanguageIdOfGame(int g) => g % ScaleParameters.LanguageCount + 1;

    public static string RegionName(int regionId) => RegionNames[regionId - 1];
    public static string LanguageName(int languageId) => LanguageNames[languageId - 1];

    // ---- catalog releases ----

    public int TitleOfCatalogRelease(int r) => _titleByCatalogRelease[r];

    public int CatalogReleaseSourceCount(int r) => _releaseSourceCount[r];

    public int CatalogReleaseFileCountForRelease(int r) => _releaseFileCount[r];

    public int CatalogReleaseFileId(int r, int slot) => _releaseFileStart[r] + slot + 1;

    public int CatalogReleaseOfGame(int g) => _catalogReleaseByGame[g];

    public int GameOfRom(long j) =>
        checked((int)(((j + 1) * Scale.DatGames + Scale.DatRoms - 1) / Scale.DatRoms - 1));

    public int CatalogReleaseFileOfRom(long j)
    {
        int game = GameOfRom(j);
        int releaseIndex = CatalogReleaseOfGame(game) - 1;
        long gameStart = RomRangeOfGame(game).Start;
        int slot = checked((int)(j - gameStart)) % _releaseFileCount[releaseIndex];
        return _releaseFileStart[releaseIndex] + slot + 1;
    }

    public byte[] CatalogReleaseFileSha1(int k) => HashBytes(PurposeSha1, (ulong)(k + 1), 20);

    public byte[] CatalogReleaseFileMd5(int k) => CatalogReleaseFileSha1(k)[..16];

    public byte[] CatalogReleaseFileCrc(int k) => CatalogReleaseFileSha1(k)[..4];

    public string CatalogReleaseFileFingerprint(int k) =>
        $"sha1:{Convert.ToHexStringLower(CatalogReleaseFileSha1(k))}";

    public long CatalogReleaseFileSize(int k) => 524_288 + k % 4_194_304;

    public long CatalogReleaseSizeBytes(int r) => Enumerable
        .Range(_releaseFileStart[r], _releaseFileCount[r])
        .Sum(CatalogReleaseFileSize);

    // ---- materialized library projection ----

    public bool MlrIsComplete(int g) => g % 5 < 4;
    public bool MlrIsOwned(int g) => g % 10 < 3;
    public bool MlrIsPlayable(int g) => MlrIsComplete(g) && MlrIsOwned(g);
    /// <summary>
    ///     Builds the exact projection the generator persisted for a library, so the
    ///     materialization scenario replays a steady-state (no-op diff) rematerialization.
    /// </summary>
    public MaterializedLibraryProjection BuildLibraryProjection(int libraryId)
    {
        var eligible = new int[Scale.Titles];
        var playable = new int[Scale.Titles];
        var owned = new bool[Scale.Titles];

        var releases = new List<MaterializedLibraryRelease>(Scale.DatGames);
        for (int g = 0; g < Scale.DatGames; g++)
        {
            int t = TitleOfGame(g);
            eligible[t]++;
            if (MlrIsPlayable(g))
            {
                playable[t]++;
            }

            owned[t] |= MlrIsOwned(g);

            releases.Add(new MaterializedLibraryRelease(
                LibraryId: libraryId,
                TitleId: t + 1,
                CatalogReleaseId: CatalogReleaseOfGame(g),
                DatGameId: g + 1,
                DatFileId: DatFileOfGame(g) + 1,
                PlatformId: PlatformOfGame(g),
                IsEligible: true,
                IsComplete: MlrIsComplete(g),
                IsOwned: MlrIsOwned(g),
                IsPlayable: MlrIsPlayable(g),
                IsBlocked: false,
                BlockReason: null,
                IsExposed: true,
                ExposureReason: "ExposedDefault"));
        }

        var titles = new List<MaterializedLibraryTitle>(Scale.Titles);
        for (int t = 0; t < Scale.Titles; t++)
        {
            titles.Add(new MaterializedLibraryTitle(
                LibraryId: libraryId,
                TitleId: t + 1,
                PlatformId: PlatformOfTitle(t),
                Genre: TitleGenre(t),
                IsVisible: true,
                IsOwned: owned[t],
                IsPlayable: playable[t] > 0,
                EligibleReleaseCount: eligible[t],
                PlayableReleaseCount: playable[t],
                ExposedReleaseCount: eligible[t],
                Availability: playable[t] > 0
                    ? LibraryTitleAvailability.Playable
                    : owned[t] ? LibraryTitleAvailability.Partial : LibraryTitleAvailability.MetadataOnly));
        }

        return new MaterializedLibraryProjection(titles, releases);
    }

    // ---- jobs ----

    public Guid JobId(int jobIndex) => GuidFrom(PurposeGuid, (ulong)jobIndex);

    public Guid JobItemId(long n) => GuidFrom(PurposeGuid ^ 1, (ulong)n);

    public int JobOfItem(long n) =>
        n < Scale.FirstJobItemCount ? 0 : 1 + (int)((n - Scale.FirstJobItemCount) % (ScaleParameters.JobCount - 1));

    public int JobItemOutcome(long n) => (n % 10) switch
    {
        < 6 => 0, // Ingested
        < 8 => 1, // Deduplicated
        _ => 2 // Rejected
    };

    public string JobItemMatchedTitleIdsJson(long n) =>
        n % 10 < 7 ? $"[{n % Scale.Titles + 1}]" : "[]";

    // ---- enrichment layer helpers ----

    public string TitleExternalId(int t) => (100_000 + t).ToString();

    public string TitleMetadataJson(int t) =>
        $"{{\"description\":\"Layered metadata for title {t + 1}\",\"genre\":\"{TitleGenre(t)}\",\"rating\":{TitleRating(t)?.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? "null"}}}";

    public int RatingBoardOfTitle(int t) => t % 3;
    public string RatingCodeOfTitle(int t) => RatingCodes[t % RatingCodes.Length];
    public int RatingAgeOfTitle(int t) => RatingAges[t % RatingAges.Length];

    // ---- file id layout: [dat files][rom files][media files] ----

    public int FileIdOfDatFile(int d) => d + 1;
    public int FileIdOfRomFile(int rf) => DatFileCount + rf + 1;
    public int FileIdOfMedia(int mediaIndex) => DatFileCount + RomFileCount + mediaIndex + 1;
    public int TotalFileCount => DatFileCount + RomFileCount + Scale.EnrichedTitles;

    public long FileSize(int fileId)
    {
        int romFileIndex = fileId - DatFileCount - 1;
        return romFileIndex >= 0 && romFileIndex < RomFileCount
            ? RomSize(SourceRomOfRomFile(romFileIndex))
            : 4096 + fileId % 1_048_576;
    }

    // ---- primitives ----

    private static int IndexToPlatform(int[] starts, int[] counts, int index)
    {
        for (int p = starts.Length - 1; p >= 0; p--)
        {
            if (index >= starts[p] && index < starts[p] + counts[p])
            {
                return p + 1;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(index), index, "Index outside dataset bounds.");
    }

    private static ulong Mix(ulong purpose, ulong index)
    {
        ulong z = ScaleParameters.Seed
                  + purpose * 0x9E3779B97F4A7C15UL
                  + index * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static byte[] HashBytes(ulong purpose, ulong index, int length)
    {
        var bytes = new byte[length];
        for (int offset = 0; offset < length; offset += 8)
        {
            ulong h = Mix(purpose + (ulong)offset, index);
            for (int i = 0; i < 8 && offset + i < length; i++)
            {
                bytes[offset + i] = (byte)(h >> (i * 8));
            }
        }

        return bytes;
    }

    private static Guid GuidFrom(ulong purpose, ulong index) =>
        new(HashBytes(purpose, index, 16));
}
