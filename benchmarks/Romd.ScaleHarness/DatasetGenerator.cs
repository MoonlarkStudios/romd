using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Romd.Admin.Application.Titles.Matching;
using Romd.Persistence;
using Romd.Persistence.Search;

namespace Romd.ScaleHarness;

/// <summary>
///     Builds the benchmark database on the configured PostgreSQL server: one database per scale,
///     production schema via the real <c>MigrateAsync()</c>, deterministic taxonomy + bulk data
///     through binary COPY with the persisted search documents written the way the production
///     interceptor writes them, identity sequences reseeded past the explicit ids, relational and
///     search-index truth checks, then ANALYZE. Production seeders are never invoked.
/// </summary>
public sealed class DatasetGenerator
{
    private readonly DeterministicDataset _data;
    private readonly HarnessOptions _options;
    private readonly Dictionary<string, long> _rowCounts = [];
    private readonly List<string> _searchChecks = [];

    public DatasetGenerator(HarnessOptions options)
    {
        _data = new DeterministicDataset(options.Scale);
        _options = options;
    }

    private ScaleParameters Scale => _data.Scale;

    public static async Task<bool> DatabaseExistsAsync(HarnessOptions options, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(options.MaintenanceConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
        command.Parameters.AddWithValue("name", options.DatabaseName);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public static async Task<string> ServerVersionAsync(HarnessOptions options, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(options.MaintenanceConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SHOW server_version", connection);
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<DatasetManifest> GenerateAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (File.Exists(_options.ManifestPath))
        {
            File.Delete(_options.ManifestPath);
        }

        await RecreateDatabaseAsync(cancellationToken);
        await MigrateAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_options.ScaleConnectionString);
        await connection.OpenAsync(cancellationToken);

        await InsertTaxonomyAsync(connection, cancellationToken);
        await InsertFilesAsync(connection, cancellationToken);
        await InsertRomFilesAsync(connection, cancellationToken);
        await InsertSourcesAsync(connection, cancellationToken);
        await InsertDatFilesAsync(connection, cancellationToken);
        await InsertSourceEntriesAsync(connection, cancellationToken);
        await InsertDatGamesAsync(connection, cancellationToken);
        await InsertDatRomsAsync(connection, cancellationToken);
        await InsertTitlesAsync(connection, cancellationToken);
        await InsertTrackedTitlesAsync(connection, cancellationToken);
        await InsertTitleSourceLinksAsync(connection, cancellationToken);
        await InsertGameJunctionsAsync(connection, cancellationToken);
        await InsertCatalogReleasesAsync(connection, cancellationToken);
        await InsertCatalogReleaseTaxonomyAsync(connection, cancellationToken);
        await InsertCatalogReleaseSourcesAsync(connection, cancellationToken);
        await InsertCatalogReleaseFilesAsync(connection, cancellationToken);
        await InsertCatalogReleaseFileSourcesAsync(connection, cancellationToken);
        await InsertMaterializedProjectionsAsync(connection, cancellationToken);
        await InsertJobsAsync(connection, cancellationToken);
        await InsertJobItemsAsync(connection, cancellationToken);
        await InsertEnrichmentLayersAsync(connection, cancellationToken);

        VerifyRowCountsMatchSpec();
        await ReseedIdentitySequencesAsync(connection, cancellationToken);
        await VerifyRelationalTruthAsync(connection, cancellationToken);
        await VerifySearchIndexAsync(connection, cancellationToken);
        await ExecuteAsync(connection, "ANALYZE;", cancellationToken);
        stopwatch.Stop();

        var manifest = new DatasetManifest(
            ScaleParameters.SpecVersion,
            Scale.Name,
            ScaleParameters.Seed,
            _options.DatabaseName,
            DateTimeOffset.UtcNow,
            stopwatch.Elapsed.TotalSeconds,
            _data.JobId(0),
            _data.LargestPlatformId,
            DeterministicDataset.SearchToken,
            _data.ExpectedSagaTitleCount(),
            new Dictionary<string, long>(_rowCounts),
            [.. _searchChecks]);
        await manifest.WriteAsync(_options.ManifestPath, cancellationToken);
        return manifest;
    }

    /// <summary>Drops and recreates the scale database; the dev server's single role owns it.</summary>
    private async Task RecreateDatabaseAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_options.MaintenanceConnectionString);
        await connection.OpenAsync(cancellationToken);
        string database = $"\"{_options.DatabaseName}\"";
        await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS {database} WITH (FORCE);", cancellationToken);
        await ExecuteAsync(
            connection,
            $"CREATE DATABASE {database} TEMPLATE template0 ENCODING 'UTF8' LC_COLLATE 'C' LC_CTYPE 'C';",
            cancellationToken);
    }

    private async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var provider = HarnessServices.Build(_options.ScaleConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        await context.Database.MigrateAsync(cancellationToken);
        NpgsqlConnection.ClearAllPools();
    }

    /// <summary>Asserts the inserted populations equal the current derived ratios exactly.</summary>
    private void VerifyRowCountsMatchSpec()
    {
        var expected = new (string Table, long Count)[]
        {
            ("DatGames", Scale.DatGames),
            ("Titles", Scale.Titles),
            ("DatRoms", Scale.DatRoms),
            ("CatalogSources", _data.DatFileCount),
            ("DatSources", _data.DatFileCount),
            ("SourceEntries", Scale.SourceEntries),
            ("TitleSourceLinks", Scale.TitleSourceLinks),
            ("CatalogReleases", Scale.CatalogReleases),
            ("CatalogReleaseRegions", Scale.CatalogReleases),
            ("CatalogReleaseLanguages", Scale.CatalogReleases),
            ("CatalogReleaseSources", Scale.DatGames),
            ("CatalogReleaseFiles", _data.CatalogReleaseFileCount),
            ("CatalogReleaseFileSources", Scale.DatRoms),
            ("MaterializedLibraryTitles", Scale.MaterializedLibraryTitles),
            ("MaterializedLibraryReleases", Scale.MaterializedLibraryReleases),
            ("JobItems", Scale.JobItems),
            ("TitleMetadataLayers", Scale.EnrichedTitles)
        };

        var mismatches = expected
            .Where(e => _rowCounts.GetValueOrDefault(e.Table) != e.Count)
            .Select(e => $"{e.Table}: expected {e.Count}, inserted {_rowCounts.GetValueOrDefault(e.Table)}")
            .ToList();
        if (mismatches.Count > 0)
        {
            throw new InvalidOperationException(
                $"Generated populations diverged from dataset spec v{ScaleParameters.SpecVersion}: {string.Join("; ", mismatches)}.");
        }
    }

    /// <summary>
    ///     Bulk loads write explicit ids; PostgreSQL identity sequences do not advance past them, so
    ///     every identity column is moved to its current maximum before any scenario inserts rows.
    /// </summary>
    private static async Task ReseedIdentitySequencesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(
            connection,
            """
            DO $reseed$
            DECLARE
                identity_column record;
                max_id bigint;
            BEGIN
                FOR identity_column IN
                    SELECT table_name, column_name
                    FROM information_schema.columns
                    WHERE table_schema = 'romd' AND is_identity = 'YES'
                LOOP
                    EXECUTE format('SELECT COALESCE(MAX(%I), 0) FROM romd.%I', identity_column.column_name, identity_column.table_name)
                        INTO max_id;
                    IF max_id > 0 THEN
                        PERFORM setval(
                            pg_get_serial_sequence(format('romd.%I', identity_column.table_name), identity_column.column_name),
                            max_id,
                            true);
                    END IF;
                END LOOP;
            END
            $reseed$;
            """,
            ct);
    }

    /// <summary>
    ///     Guards the semantic topology the scale scenarios depend on. These checks deliberately
    ///     run inside every fresh generation so schema-valid but impossible synthetic data cannot
    ///     become a reusable manifest. Foreign keys are enforced by PostgreSQL during COPY.
    /// </summary>
    private static async Task VerifyRelationalTruthAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var checks = new (string Name, string Sql)[]
        {
            ("release SourceCount",
                """
                SELECT COUNT(*) FROM romd."CatalogReleases" r
                WHERE r."SourceCount" <> (
                    SELECT COUNT(DISTINCT s."SourceEntryId")
                    FROM romd."CatalogReleaseSources" s
                    WHERE s."CatalogReleaseId" = r."Id");
                """),
            ("release file aggregates",
                """
                SELECT COUNT(*) FROM romd."CatalogReleases" r
                WHERE r."FileCount" <> (SELECT COUNT(*) FROM romd."CatalogReleaseFiles" f WHERE f."CatalogReleaseId" = r."Id")
                   OR r."SizeBytes" <> COALESCE((SELECT SUM(f."Size") FROM romd."CatalogReleaseFiles" f WHERE f."CatalogReleaseId" = r."Id"), 0);
                """),
            ("release asserted-title topology",
                """
                SELECT COUNT(*)
                FROM romd."CatalogReleaseSources" s
                JOIN romd."CatalogReleases" r ON r."Id" = s."CatalogReleaseId"
                WHERE s."AssertedTitleId" IS NULL OR s."AssertedTitleId" <> r."CatalogTitleId";
                """),
            ("release file provenance topology",
                """
                SELECT COUNT(*)
                FROM romd."CatalogReleaseFileSources" fs
                JOIN romd."CatalogReleaseFiles" f ON f."Id" = fs."CatalogReleaseFileId"
                JOIN romd."DatGames" g ON g."SourceEntryId" = fs."SourceEntryId"
                    AND fs."ProviderClaimKey" = 'game:' || g."Id"
                JOIN romd."DatRoms" dr ON dr."DatGameId" = g."Id"
                    AND fs."ProviderRequirementKey" = 'rom:' || dr."Id"
                JOIN romd."CatalogReleaseSources" rs ON rs."SourceEntryId" = g."SourceEntryId"
                WHERE fs."RequirementKind" <> 'Rom'
                   OR f."CatalogReleaseId" <> rs."CatalogReleaseId"
                   OR f."Sha1" IS NULL OR f."Sha1" <> dr."Sha1"
                   OR f."Size" <> dr."Size"
                   OR f."Crc" IS NULL OR f."Crc" <> dr."Crc"
                   OR f."Md5" IS NULL OR f."Md5" <> dr."Md5";
                """),
            ("release file fingerprint topology",
                """
                SELECT COUNT(*) FROM romd."CatalogReleaseFiles" f
                WHERE f."Sha1" IS NULL OR f."FileFingerprint" <> 'sha1:' || encode(f."Sha1", 'hex');
                """),
            ("stored ROM byte identity",
                """
                SELECT COUNT(*)
                FROM romd."DatRoms" dr
                JOIN romd."RomFiles" rf ON rf."Id" = dr."RomFileId"
                JOIN romd."Files" f ON f."Id" = rf."FileId"
                WHERE rf."Sha1" <> dr."Sha1"
                   OR rf."Md5" <> dr."Md5"
                   OR rf."Crc32" <> dr."Crc"
                   OR f."Size" <> dr."Size"
                   OR f."SizeOnDisk" <> dr."Size"
                   OR f."IsCompressed";
                """),
            ("release region topology",
                """
                SELECT COUNT(*) FROM romd."CatalogReleases" r
                WHERE (SELECT COUNT(*) FROM romd."CatalogReleaseRegions" rr WHERE rr."CatalogReleaseId" = r."Id") <> 1
                   OR NOT EXISTS (
                       SELECT 1 FROM romd."CatalogReleaseRegions" rr
                       JOIN romd."Regions" region ON region."Id" = rr."RegionId"
                       WHERE rr."CatalogReleaseId" = r."Id" AND region."Name" = r."Region");
                """),
            ("release language topology",
                """
                SELECT COUNT(*) FROM romd."CatalogReleases" r
                WHERE (SELECT COUNT(*) FROM romd."CatalogReleaseLanguages" rl WHERE rl."CatalogReleaseId" = r."Id") <> 1
                   OR NOT EXISTS (
                       SELECT 1 FROM romd."CatalogReleaseLanguages" rl
                       JOIN romd."GameLanguages" language ON language."Id" = rl."GameLanguageId"
                       WHERE rl."CatalogReleaseId" = r."Id" AND language."Code" = lower(r."Language"));
                """),
            ("canonical files without provenance",
                """
                SELECT COUNT(*) FROM romd."CatalogReleaseFiles" f
                WHERE NOT EXISTS (
                    SELECT 1 FROM romd."CatalogReleaseFileSources" fs WHERE fs."CatalogReleaseFileId" = f."Id");
                """),
            ("DAT aggregate counts",
                """
                SELECT COUNT(*)
                FROM romd."DatFiles" f
                WHERE f."GameCount" <> (SELECT COUNT(*) FROM romd."DatGames" g WHERE g."DatFileId" = f."Id")
                   OR f."RomCount" <> (
                       SELECT COUNT(*) FROM romd."DatRoms" r
                       JOIN romd."DatGames" g ON g."Id" = r."DatGameId"
                       WHERE g."DatFileId" = f."Id")
                   OR f."DiskCount" <> (
                       SELECT COUNT(*) FROM romd."DatDisks" d
                       JOIN romd."DatGames" g ON g."Id" = d."DatGameId"
                       WHERE g."DatFileId" = f."Id");
                """),
            ("duplicate release source entries",
                """
                SELECT COUNT(*) FROM (
                    SELECT "SourceEntryId" FROM romd."CatalogReleaseSources" GROUP BY "SourceEntryId" HAVING COUNT(*) <> 1) d;
                """),
            ("duplicate provider file sources",
                """
                SELECT COUNT(*) FROM (
                    SELECT "SourceEntryId", "ProviderClaimKey", "RequirementKind", "ProviderRequirementKey"
                    FROM romd."CatalogReleaseFileSources"
                    GROUP BY "SourceEntryId", "ProviderClaimKey", "RequirementKind", "ProviderRequirementKey"
                    HAVING COUNT(*) <> 1) d;
                """)
        };

        var violations = new List<string>();
        foreach (var check in checks)
        {
            long count = await ScalarAsync(connection, check.Sql, ct);
            if (count != 0)
            {
                violations.Add($"{check.Name}: {count}");
            }
        }

        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                $"Generated relational truth checks failed: {string.Join("; ", violations)}.");
        }
    }

    /// <summary>
    ///     The stored tsvector is derived by PostgreSQL from the persisted search document, so the
    ///     checks are: no searchable row was loaded without its document, the GIN indexes exist, and a
    ///     sampled prefix query agrees with the generator's own expectation.
    /// </summary>
    private async Task VerifySearchIndexAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        long emptyTitles = await ScalarAsync(connection,
            "SELECT COUNT(*) FROM romd.\"Titles\" WHERE \"SearchDocument\" = '';", ct);
        long emptyGames = await ScalarAsync(connection,
            "SELECT COUNT(*) FROM romd.\"DatGames\" WHERE \"SearchDocument\" = '';", ct);
        if (emptyTitles != 0 || emptyGames != 0)
        {
            throw new InvalidOperationException(
                $"Search documents missing: Titles={emptyTitles}, DatGames={emptyGames}.");
        }

        _searchChecks.Add(
            $"Every Titles and DatGames row carries a search document ({Scale.Titles:N0} / {Scale.DatGames:N0}).");

        long ginIndexes = await ScalarAsync(connection,
            """
            SELECT COUNT(*) FROM pg_indexes
            WHERE schemaname = 'romd'
              AND indexname IN ('IX_Titles_SearchVector', 'IX_DatGames_SearchVector')
              AND indexdef ILIKE '%USING gin%';
            """, ct);
        if (ginIndexes != 2)
        {
            throw new InvalidOperationException($"Expected both GIN search indexes, found {ginIndexes}.");
        }

        _searchChecks.Add("GIN indexes present on Titles.SearchVector and DatGames.SearchVector.");

        string? tsQuery = SearchQuery.ToTsQueryText(DeterministicDataset.SearchToken);
        long sagaMatches = await ScalarAsync(connection,
            $"""
            SELECT COUNT(*) FROM romd."Titles"
            WHERE "SearchVector" @@ to_tsquery('{SearchDocuments.TextSearchConfiguration}', '{tsQuery}');
            """, ct);
        int expected = _data.ExpectedSagaTitleCount();
        if (sagaMatches != expected)
        {
            throw new InvalidOperationException(
                $"Sampled search mismatch: '{tsQuery}' matched {sagaMatches} titles, generator expected {expected}.");
        }

        _searchChecks.Add($"Sampled prefix query '{tsQuery}' == generator expectation ({expected:N0} titles).");
    }

    // ---- table loads ----

    private async Task InsertTaxonomyAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        long now = DeterministicDataset.BaseTime.ToUnixTimeMilliseconds();
        Guid actor = DeterministicDataset.SystemUserId;

        await using (var platforms = new BulkInserter(connection, "Platforms",
                         ["Id", "Name", "ShortName", "Manufacturer", "CatalogRebuildState", "CatalogRebuiltAt", "CreatedAt", "CreatedByUserId"]))
        {
            for (int p = 1; p <= ScaleParameters.PlatformCount; p++)
            {
                await platforms.AddAsync([p, $"Platform {p:D2}", $"plat{p:D2}", "Otterworks", 0, null, now, actor], ct);
            }

            _rowCounts["Platforms"] = platforms.RowsInserted;
        }

        await using (var regions = new BulkInserter(connection, "Regions",
                         ["Id", "Name", "SortOrder", "IsAutoCreated", "CreatedAt", "CreatedByUserId"]))
        {
            for (int r = 1; r <= ScaleParameters.RegionCount; r++)
            {
                await regions.AddAsync([r, DeterministicDataset.RegionName(r), r, false, now, actor], ct);
            }

            _rowCounts["Regions"] = regions.RowsInserted;
        }

        await using (var languages = new BulkInserter(connection, "GameLanguages",
                         ["Id", "Name", "Code", "SortOrder", "IsAutoCreated", "CreatedAt", "CreatedByUserId"]))
        {
            for (int l = 1; l <= ScaleParameters.LanguageCount; l++)
            {
                await languages.AddAsync(
                    [l, DeterministicDataset.LanguageName(l), DeterministicDataset.LanguageName(l)[..2].ToLowerInvariant(), l, false, now, actor],
                    ct);
            }

            _rowCounts["GameLanguages"] = languages.RowsInserted;
        }

        await using (var libraries = new BulkInserter(connection, "Libraries",
                         ["Id", "Name", "ConfigurationJson", "ConfigurationState", "ConfigurationError", "IsDefault",
                          "NeedsMaterialization", "LastMaterializedAt", "ItemCount", "CreatedAt", "CreatedByUserId",
                          "UpdatedAt", "UpdatedByUserId"]))
        {
            for (int l = 1; l <= ScaleParameters.LibraryCount; l++)
            {
                await libraries.AddAsync(
                    [l, l == 1 ? "Main" : "Second Library", "{}", "Valid", null, l == 1, false, now,
                     Scale.Titles, now, actor, now, actor], ct);
            }

            _rowCounts["Libraries"] = libraries.RowsInserted;
        }
    }

    private async Task InsertFilesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        long now = DeterministicDataset.BaseTime.ToUnixTimeMilliseconds();
        await using var files = new BulkInserter(connection, "Files",
            ["Id", "Sha256", "Size", "SizeOnDisk", "IsCompressed", "CreatedAt", "CreatedByUserId"]);

        for (int f = 1; f <= _data.TotalFileCount; f++)
        {
            long size = _data.FileSize(f);
            await files.AddAsync([f, _data.FileSha256(f), size, size, false, now, DeterministicDataset.SystemUserId], ct);
        }

        _rowCounts["Files"] = files.RowsInserted;
    }

    private async Task InsertRomFilesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        long now = DeterministicDataset.BaseTime.ToUnixTimeMilliseconds();
        await using var romFiles = new BulkInserter(connection, "RomFiles",
            ["Id", "FileId", "OriginalFilename", "Sha1", "Md5", "Crc32", "CreatedAt", "CreatedByUserId"]);

        for (int romFileIndex = 0; romFileIndex < _data.RomFileCount; romFileIndex++)
        {
            int romFileId = romFileIndex + 1;
            long j = _data.SourceRomOfRomFile(romFileIndex);
            await romFiles.AddAsync(
                [romFileId, _data.FileIdOfRomFile(romFileIndex), $"rom_{j:D8}.bin", _data.RomSha1(j),
                 _data.RomMd5(j), _data.RomCrc(j), now, DeterministicDataset.SystemUserId], ct);
        }

        _rowCounts["RomFiles"] = romFiles.RowsInserted;
    }

    private async Task InsertSourcesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        long now = DeterministicDataset.BaseTime.ToUnixTimeMilliseconds();
        Guid actor = DeterministicDataset.SystemUserId;

        await using (var catalogSources = new BulkInserter(connection, "CatalogSources",
                         ["Id", "Kind", "Status", "Name", "CreatedAt", "CreatedByUserId"]))
        {
            for (int d = 0; d < _data.DatFileCount; d++)
            {
                await catalogSources.AddAsync([d + 1, "Dat", "Active", null, now, actor], ct);
            }

            _rowCounts["CatalogSources"] = catalogSources.RowsInserted;
        }

        await using (var datSources = new BulkInserter(connection, "DatSources",
                         ["Id", "CatalogSourceId", "CreatedAt", "CreatedByUserId"]))
        {
            for (int d = 0; d < _data.DatFileCount; d++)
            {
                await datSources.AddAsync([d + 1, d + 1, now, actor], ct);
            }

            _rowCounts["DatSources"] = datSources.RowsInserted;
        }
    }

    private async Task InsertDatFilesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        long now = DeterministicDataset.BaseTime.ToUnixTimeMilliseconds();
        await using var datFiles = new BulkInserter(connection, "DatFiles",
            ["Id", "Name", "Description", "OriginalFilename", "Type", "Author", "Version", "Url", "PlatformId",
             "FileId", "GameCount", "RomCount", "DiskCount", "DatSourceId", "Lifecycle", "SupersededAt",
             "CreatedAt", "CreatedByUserId", "UpdatedAt", "UpdatedByUserId"]);

        for (int d = 0; d < _data.DatFileCount; d++)
        {
            int platformId = _data.PlatformOfDatFile(d);
            await datFiles.AddAsync(
                [d + 1, $"Platform {platformId:D2} Set {d + 1:D4}", "", $"dat_{d + 1:D4}.dat", "rom",
                 "ScaleHarness", "1.0", null, platformId, _data.FileIdOfDatFile(d),
                 _data.GameCountForDatFile(d), _data.RomCountForDatFile(d), 0, d + 1, "Active", null,
                 now, DeterministicDataset.SystemUserId, null, null], ct);
        }

        _rowCounts["DatFiles"] = datFiles.RowsInserted;
    }

    private async Task InsertSourceEntriesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        long now = DeterministicDataset.BaseTime.ToUnixTimeMilliseconds();
        await using var entries = new BulkInserter(connection, "SourceEntries",
            ["Id", "CatalogSourceId", "EntryKey", "Name", "PlatformId", "HasLocalPayload", "LastReconcileRunId",
             "CreatedAt", "CreatedByUserId"]);

        for (int g = 0; g < Scale.DatGames; g++)
        {
            string gameName = _data.GameName(g);
            await entries.AddAsync(
                [g + 1, _data.DatFileOfGame(g) + 1, gameName, gameName, _data.PlatformOfGame(g),
                 _data.GameHasLocalPayload(g), null,
                 now, DeterministicDataset.SystemUserId], ct);
        }

        _rowCounts["SourceEntries"] = entries.RowsInserted;
    }

    private async Task InsertDatGamesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        long now = DeterministicDataset.BaseTime.ToUnixTimeMilliseconds();
        await using var games = new BulkInserter(connection, "DatGames",
            ["Id", "DatFileId", "SourceEntryId", "Name", "Description", "Category", "CloneOf", "RomOf", "Year", "Manufacturer",
             "Region", "Language", "Revision", "DevelopmentStatus", "IsBios", "CreatedAt", "CreatedByUserId", "SearchDocument"]);

        for (int g = 0; g < Scale.DatGames; g++)
        {
            string name = _data.GameName(g);
            string? description = _data.GameDescription(g);
            await games.AddAsync(
                [g + 1, _data.DatFileOfGame(g) + 1, g + 1, name, description, null, null, null,
                 _data.GameYear(g), _data.GameManufacturer(g), DeterministicDataset.RegionName(_data.RegionIdOfGame(g)),
                 null, null, null, false, now, DeterministicDataset.SystemUserId, SearchDocuments.For(name, description)], ct);
        }

        _rowCounts["DatGames"] = games.RowsInserted;
    }

    private async Task InsertDatRomsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        long now = DeterministicDataset.BaseTime.ToUnixTimeMilliseconds();
        await using var roms = new BulkInserter(connection, "DatRoms",
            ["Id", "DatGameId", "Name", "Size", "Crc", "Md5", "Sha1", "Status", "Serial", "RomFileId",
             "CreatedAt", "CreatedByUserId"]);

        for (int g = 0; g < Scale.DatGames; g++)
        {
            (long start, long end) = _data.RomRangeOfGame(g);
            for (long j = start; j < end; j++)
            {
                byte[] sha1 = _data.RomSha1(j);
                int romFileId = _data.RomFileIdOfRom(j);
                await roms.AddAsync(
                    [j + 1, g + 1, $"rom_{j:D8}.bin", _data.RomSize(j), _data.RomCrc(j), _data.RomMd5(j), sha1,
                     null, null, romFileId > 0 ? romFileId : null,
                     now, DeterministicDataset.SystemUserId], ct);
            }
        }

        _rowCounts["DatRoms"] = roms.RowsInserted;
    }

    private async Task InsertTitlesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        long now = DeterministicDataset.BaseTime.ToUnixTimeMilliseconds();
        await using var titles = new BulkInserter(connection, "Titles",
            ["Id", "PlatformId", "HasLocalPayload", "Name", "NormalizedName", "EnrichmentStatus", "FieldProvenanceJson",
             "FieldSourceOverridesJson", "ScreenshotPrefsJson", "Description", "Developer", "Publisher", "Genre",
             "Players", "Rating", "ReleaseDate", "CatalogState", "ConservativeMinimumAge",
             "LastEnrichedAt", "CreatedAt", "CreatedByUserId", "UpdatedAt", "UpdatedByUserId", "SearchDocument"]);

        for (int t = 0; t < Scale.Titles; t++)
        {
            bool enriched = _data.TitleEnriched(t);
            string name = _data.TitleName(t);
            string? description = _data.TitleDescription(t);
            await titles.AddAsync(
                [t + 1, _data.PlatformOfTitle(t), _data.TitleHasLocalPayload(t),
                 name, TitleNormalizer.Normalize(name),
                 enriched ? "Completed" : "None", "{}", "{}", "{}", description,
                 _data.TitlePublisher(t), _data.TitlePublisher(t), _data.TitleGenre(t), _data.TitlePlayers(t),
                 _data.TitleRating(t), _data.TitleReleaseDate(t)?.ToString("yyyy-MM-dd"), 0,
                 enriched ? _data.RatingAgeOfTitle(t) : null,
                 enriched ? now : null, now, DeterministicDataset.SystemUserId, null, null,
                 SearchDocuments.For(name, description)], ct);
        }

        _rowCounts["Titles"] = titles.RowsInserted;
    }

    private async Task InsertTrackedTitlesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        long now = DeterministicDataset.BaseTime.ToUnixTimeMilliseconds();
        await using var trackedTitles = new BulkInserter(connection, "TrackedTitles",
            ["TitleId", "PinnedCatalogReleaseId", "CreatedAt", "UpdatedAt"]);

        for (int t = 0; t < Scale.Titles; t++)
        {
            if (_data.TitleTracked(t))
            {
                await trackedTitles.AddAsync([t + 1, null, now, now], ct);
            }
        }

        _rowCounts["TrackedTitles"] = trackedTitles.RowsInserted;
    }

    private async Task InsertTitleSourceLinksAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        long now = DeterministicDataset.BaseTime.ToUnixTimeMilliseconds();
        await using var links = new BulkInserter(connection, "TitleSourceLinks",
            ["SourceEntryId", "TitleId", "CreatedAt", "CreatedByUserId", "UpdatedAt", "UpdatedByUserId"]);

        for (int g = 0; g < Scale.DatGames; g++)
        {
            await links.AddAsync(
                [g + 1, _data.TitleOfGame(g) + 1, now, DeterministicDataset.SystemUserId, null, null], ct);
        }

        _rowCounts["TitleSourceLinks"] = links.RowsInserted;
    }

    private async Task InsertGameJunctionsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        long now = DeterministicDataset.BaseTime.ToUnixTimeMilliseconds();

        await using (var gameRegions = new BulkInserter(connection, "DatGameRegions",
                         ["DatGameId", "RegionId", "CreatedAt", "CreatedByUserId"]))
        {
            for (int g = 0; g < Scale.DatGames; g++)
            {
                await gameRegions.AddAsync([g + 1, _data.RegionIdOfGame(g), now, DeterministicDataset.SystemUserId], ct);
            }

            _rowCounts["DatGameRegions"] = gameRegions.RowsInserted;
        }

        await using (var gameLanguages = new BulkInserter(connection, "DatGameLanguages",
                         ["DatGameId", "GameLanguageId", "CreatedAt", "CreatedByUserId"]))
        {
            for (int g = 0; g < Scale.DatGames; g++)
            {
                await gameLanguages.AddAsync([g + 1, _data.LanguageIdOfGame(g), now, DeterministicDataset.SystemUserId], ct);
            }

            _rowCounts["DatGameLanguages"] = gameLanguages.RowsInserted;
        }
    }

    private async Task InsertCatalogReleasesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        long now = DeterministicDataset.BaseTime.ToUnixTimeMilliseconds();
        await using var releases = new BulkInserter(connection, "CatalogReleases",
            ["Id", "CatalogTitleId", "PlatformId", "Name", "Region", "Language", "Revision", "Fingerprint",
             "PrimarySha1", "FileCount", "SizeBytes", "SourceCount", "HasTitleConflict", "CreatedAt",
             "CreatedByUserId", "UpdatedAt"]);

        for (int r = 0; r < Scale.CatalogReleases; r++)
        {
            int t = _data.TitleOfCatalogRelease(r);
            await releases.AddAsync(
                [r + 1, t + 1, _data.PlatformOfTitle(t), $"{_data.TitleName(t)} (Release {r + 1})", "USA", "En",
                 null, $"fp{r + 1:D9}", null, _data.CatalogReleaseFileCountForRelease(r),
                 _data.CatalogReleaseSizeBytes(r), _data.CatalogReleaseSourceCount(r), false, now,
                 DeterministicDataset.SystemUserId, now], ct);
        }

        _rowCounts["CatalogReleases"] = releases.RowsInserted;
    }

    private async Task InsertCatalogReleaseSourcesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var sources = new BulkInserter(connection, "CatalogReleaseSources",
            ["Id", "CatalogReleaseId", "SourceEntryId", "ProviderClaimKey", "AssertedTitleId"]);

        for (int g = 0; g < Scale.DatGames; g++)
        {
            await sources.AddAsync(
                [g + 1, _data.CatalogReleaseOfGame(g), g + 1, $"game:{g + 1}",
                 _data.TitleOfGame(g) + 1], ct);
        }

        _rowCounts["CatalogReleaseSources"] = sources.RowsInserted;
    }

    private async Task InsertCatalogReleaseTaxonomyAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using (var regions = new BulkInserter(connection, "CatalogReleaseRegions",
                         ["CatalogReleaseId", "RegionId"]))
        {
            for (int r = 0; r < Scale.CatalogReleases; r++)
            {
                await regions.AddAsync([r + 1, 1], ct);
            }

            _rowCounts["CatalogReleaseRegions"] = regions.RowsInserted;
        }

        await using (var languages = new BulkInserter(connection, "CatalogReleaseLanguages",
                         ["CatalogReleaseId", "GameLanguageId"]))
        {
            for (int r = 0; r < Scale.CatalogReleases; r++)
            {
                await languages.AddAsync([r + 1, 1], ct);
            }

            _rowCounts["CatalogReleaseLanguages"] = languages.RowsInserted;
        }
    }

    private async Task InsertCatalogReleaseFilesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var releaseFiles = new BulkInserter(connection, "CatalogReleaseFiles",
            ["Id", "CatalogReleaseId", "Name", "Size", "Crc", "Md5", "Sha1", "Status", "IsDisk", "FileFingerprint"]);

        int k = 0;
        for (int r = 0; r < Scale.CatalogReleases; r++)
        {
            for (int slot = 0; slot < _data.CatalogReleaseFileCountForRelease(r); slot++, k++)
            {
                int fileId = _data.CatalogReleaseFileId(r, slot);
                await releaseFiles.AddAsync(
                    [fileId, r + 1, $"cr{r + 1:D8}-f{slot}.bin", _data.CatalogReleaseFileSize(k),
                     _data.CatalogReleaseFileCrc(k), _data.CatalogReleaseFileMd5(k),
                     _data.CatalogReleaseFileSha1(k), null, false, _data.CatalogReleaseFileFingerprint(k)], ct);
            }
        }

        _rowCounts["CatalogReleaseFiles"] = releaseFiles.RowsInserted;
    }

    private async Task InsertCatalogReleaseFileSourcesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var sources = new BulkInserter(connection, "CatalogReleaseFileSources",
            ["Id", "CatalogReleaseFileId", "SourceEntryId", "ProviderClaimKey", "RequirementKind", "ProviderRequirementKey"]);

        for (long j = 0; j < Scale.DatRoms; j++)
        {
            int gameId = _data.GameOfRom(j) + 1;
            await sources.AddAsync(
                [j + 1, _data.CatalogReleaseFileOfRom(j), gameId, $"game:{gameId}", "Rom", $"rom:{j + 1}"],
                ct);
        }

        _rowCounts["CatalogReleaseFileSources"] = sources.RowsInserted;
    }

    private async Task InsertMaterializedProjectionsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        long titleRowCount = 0, releaseRowCount = 0;

        // One BulkInserter (and thus one transaction) at a time per connection.
        for (int library = 1; library <= ScaleParameters.LibraryCount; library++)
        {
            var projection = _data.BuildLibraryProjection(library);

            await using (var titleRows = new BulkInserter(connection, "MaterializedLibraryTitles",
                             ["Id", "LibraryId", "TitleId", "PlatformId", "Genre", "IsVisible", "IsOwned", "IsPlayable",
                              "EligibleReleaseCount", "PlayableReleaseCount", "ExposedReleaseCount", "Availability"]))
            {
                for (int i = 0; i < projection.Titles.Count; i++)
                {
                    var row = projection.Titles[i];
                    await titleRows.AddAsync(
                        [(library - 1) * Scale.Titles + i + 1, row.LibraryId, row.TitleId, row.PlatformId, row.Genre,
                         row.IsVisible, row.IsOwned, row.IsPlayable, row.EligibleReleaseCount, row.PlayableReleaseCount,
                         row.ExposedReleaseCount, row.Availability.ToString()], ct);
                }

                titleRowCount += titleRows.RowsInserted;
            }

            await using (var releaseRows = new BulkInserter(connection, "MaterializedLibraryReleases",
                             ["Id", "LibraryId", "TitleId", "CatalogReleaseId", "DatGameId", "DatFileId", "PlatformId",
                              "IsEligible", "IsComplete", "IsOwned", "IsPlayable", "IsBlocked", "BlockReason",
                              "IsExposed", "ExposureReason"]))
            {
                for (int i = 0; i < projection.Releases.Count; i++)
                {
                    var row = projection.Releases[i];
                    await releaseRows.AddAsync(
                        [(library - 1) * Scale.DatGames + i + 1, row.LibraryId, row.TitleId, row.CatalogReleaseId,
                         row.DatGameId, row.DatFileId, row.PlatformId, row.IsEligible, row.IsComplete, row.IsOwned,
                         row.IsPlayable, row.IsBlocked, row.BlockReason, row.IsExposed, row.ExposureReason], ct);
                }

                releaseRowCount += releaseRows.RowsInserted;
            }
        }

        _rowCounts["MaterializedLibraryTitles"] = titleRowCount;
        _rowCounts["MaterializedLibraryReleases"] = releaseRowCount;
    }

    private async Task InsertJobsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        long now = DeterministicDataset.BaseTime.ToUnixTimeMilliseconds();
        await using var jobs = new BulkInserter(connection, "Jobs",
            ["Id", "CorrelationId", "SourceFilename", "PlatformId", "Phase", "HangfireJobId", "CurrentItem",
             "ErrorsJson", "StartedAt", "CompletedAt", "IsArchived", "ArchivedAt", "CreatedByUserId", "UpdatedAt",
             "JobType", "CreatedAt", "LastAttemptErrorTruncated"]);

        for (int j = 0; j < ScaleParameters.JobCount; j++)
        {
            await jobs.AddAsync(
                [_data.JobId(j), _data.JobId(j), $"upload_{j + 1:D2}.zip", j % ScaleParameters.PlatformCount + 1,
                 "Completed", null, null, "[]", now, now, false, null, null, now, "upload", now, false], ct);
        }

        _rowCounts["Jobs"] = jobs.RowsInserted;
    }

    private async Task InsertJobItemsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        long baseMs = DeterministicDataset.BaseTime.ToUnixTimeMilliseconds();
        await using var items = new BulkInserter(connection, "JobItems",
            ["Id", "JobId", "Kind", "FileName", "SizeBytes", "Outcome", "RomFileId", "DatFileId", "PlatformId",
             "MatchedTitleIdsJson", "GameCount", "Error", "CreatedAt"]);

        for (long n = 0; n < Scale.JobItems; n++)
        {
            int outcome = _data.JobItemOutcome(n);
            await items.AddAsync(
                [_data.JobItemId(n), _data.JobId(_data.JobOfItem(n)), 0, $"file_{n:D8}.zip", 524_288 + n % 8_388_608,
                 outcome, null, null, (int)(n % ScaleParameters.PlatformCount) + 1,
                 _data.JobItemMatchedTitleIdsJson(n), null, outcome == 2 ? "No catalog match" : null,
                 baseMs + n * 10], ct);
        }

        _rowCounts["JobItems"] = items.RowsInserted;
    }

    private async Task InsertEnrichmentLayersAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        long now = DeterministicDataset.BaseTime.ToUnixTimeMilliseconds();
        Guid actor = DeterministicDataset.SystemUserId;

        var enrichedTitles = Enumerable.Range(0, Scale.Titles).Where(_data.TitleEnriched).ToList();

        // One BulkInserter (and thus one transaction) at a time per connection.
        await using (var layers = new BulkInserter(connection, "TitleMetadataLayers",
                         ["Id", "TitleId", "SourceType", "SourceId", "MetadataJson", "CreatedAt", "CreatedByUserId", "UpdatedAt", "UpdatedByUserId"]))
        {
            for (int i = 0; i < enrichedTitles.Count; i++)
            {
                await layers.AddAsync(
                    [i + 1, enrichedTitles[i] + 1, 2, "igdb", _data.TitleMetadataJson(enrichedTitles[i]), now, actor, null, null], ct);
            }

            _rowCounts["TitleMetadataLayers"] = layers.RowsInserted;
        }

        await using (var externalIds = new BulkInserter(connection, "TitleExternalIds",
                         ["Id", "TitleId", "Provider", "ExternalId", "MatchConfidence", "IsConfirmed", "CreatedAt", "CreatedByUserId", "UpdatedAt", "UpdatedByUserId"]))
        {
            for (int i = 0; i < enrichedTitles.Count; i++)
            {
                await externalIds.AddAsync(
                    [i + 1, enrichedTitles[i] + 1, "igdb", _data.TitleExternalId(enrichedTitles[i]), 0.9,
                     enrichedTitles[i] % 2 == 0, now, actor, null, null], ct);
            }

            _rowCounts["TitleExternalIds"] = externalIds.RowsInserted;
        }

        await using (var media = new BulkInserter(connection, "TitleMedia",
                         ["Id", "TitleId", "Type", "FileId", "SourceId", "SourceUrl", "ContentType", "IsPrimary", "CreatedAt", "CreatedByUserId", "UpdatedAt", "UpdatedByUserId"]))
        {
            for (int i = 0; i < enrichedTitles.Count; i++)
            {
                await media.AddAsync(
                    [i + 1, enrichedTitles[i] + 1, "Cover", _data.FileIdOfMedia(i), "igdb", null, "image/jpeg",
                     true, now, actor, null, null], ct);
            }

            _rowCounts["TitleMedia"] = media.RowsInserted;
        }

        await using (var ratings = new BulkInserter(connection, "TitleContentRatings",
                         ["Id", "TitleId", "Board", "Code", "Designation", "MinimumAge", "DescriptorsJson", "Synopsis", "SourceId",
                          "ExternalRatingId", "CreatedAt", "CreatedByUserId", "UpdatedAt", "UpdatedByUserId"]))
        {
            for (int i = 0; i < enrichedTitles.Count; i++)
            {
                int t = enrichedTitles[i];
                await ratings.AddAsync(
                    [i + 1, t + 1, _data.RatingBoardOfTitle(t), _data.RatingCodeOfTitle(t), 0,
                     _data.RatingAgeOfTitle(t), "[]", null, "igdb", null, now, actor, null, null], ct);
            }

            _rowCounts["TitleContentRatings"] = ratings.RowsInserted;
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        object? result = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result);
    }
}
