-- Content delivery planning report for ROMD SQLite databases.
-- Run with:
--   sqlite3 /path/to/romd.db < docs/reports/report-content-delivery.sql

.headers on
.mode column

WITH
rom_agg AS (
    SELECT
        dr.DatGameId,
        COUNT(*) AS rom_count,
        SUM(dr.Size) AS payload_bytes,
        MAX(CASE
            WHEN lower(dr.Name) GLOB '*.cue'
              OR lower(dr.Name) GLOB '*.iso'
              OR lower(dr.Name) GLOB '*.chd'
              OR lower(dr.Name) GLOB '*.gdi'
              OR lower(dr.Name) GLOB '*.cdi'
              OR lower(dr.Name) GLOB '*.rvz'
              OR lower(dr.Name) GLOB '*.wbfs'
              OR lower(dr.Name) GLOB '*.wud'
              OR lower(dr.Name) GLOB '*.xiso'
            THEN 1 ELSE 0
        END) AS has_large_media_extension
    FROM DatRoms dr
    GROUP BY dr.DatGameId
),
disk_agg AS (
    SELECT
        dd.DatGameId,
        COUNT(*) AS disk_count
    FROM DatDisks dd
    GROUP BY dd.DatGameId
),
game_payloads AS (
    SELECT
        dg.Id AS dat_game_id,
        dg.Name AS game_name,
        COALESCE(p.ShortName, '(unassigned)') AS platform_short_name,
        COALESCE(p.Name, '(unassigned)') AS platform_name,
        COALESCE(ra.rom_count, 0) AS rom_count,
        COALESCE(ra.payload_bytes, 0) AS payload_bytes,
        COALESCE(da.disk_count, 0) AS disk_count,
        CASE
            WHEN COALESCE(da.disk_count, 0) > 0 THEN 'disk_manifest_no_size'
            WHEN COALESCE(ra.has_large_media_extension, 0) = 1 THEN 'optical_or_large_media'
            WHEN p.ShortName IN (
                'psx', 'ps2', 'ps3', 'ps4', 'ps5', 'psp', 'vita',
                'segacd', 'saturn', 'dc', 'gc', 'wii', 'wiiu', 'switch',
                'xbox', 'x360', 'xone', 'xsx',
                'tgcd', 'neogeocd', '3do', 'cdi'
            ) THEN 'optical_or_large_media'
            WHEN p.ShortName IN ('arcade', 'c64', 'amiga', 'atarist', 'dos') THEN 'mixed_or_unknown'
            ELSE 'cartridge_or_small_media'
        END AS media_category,
        CASE
            WHEN COALESCE(da.disk_count, 0) > 0 THEN 'disk/unknown size'
            WHEN COALESCE(ra.payload_bytes, 0) <= 16 * 1024 * 1024 THEN '<=16 MiB'
            WHEN COALESCE(ra.payload_bytes, 0) <= 64 * 1024 * 1024 THEN '>16-64 MiB'
            WHEN COALESCE(ra.payload_bytes, 0) <= 128 * 1024 * 1024 THEN '>64-128 MiB'
            WHEN COALESCE(ra.payload_bytes, 0) <= 256 * 1024 * 1024 THEN '>128-256 MiB'
            WHEN COALESCE(ra.payload_bytes, 0) <= 1024 * 1024 * 1024 THEN '>256 MiB-1 GiB'
            ELSE '>1 GiB'
        END AS size_bucket,
        CASE
            WHEN COALESCE(da.disk_count, 0) > 0 THEN 'range_required'
            WHEN COALESCE(ra.payload_bytes, 0) <= 64 * 1024 * 1024
             AND COALESCE(ra.has_large_media_extension, 0) = 0 THEN 'full_buffer_default'
            WHEN COALESCE(ra.payload_bytes, 0) <= 128 * 1024 * 1024
             AND COALESCE(ra.has_large_media_extension, 0) = 0 THEN 'conditional_full_buffer'
            WHEN COALESCE(ra.payload_bytes, 0) <= 256 * 1024 * 1024 THEN 'launch_cache_preferred'
            ELSE 'range_required'
        END AS recommended_delivery
    FROM DatGames dg
    JOIN DatFiles df ON df.Id = dg.DatFileId
    LEFT JOIN Platforms p ON p.Id = df.PlatformId
    LEFT JOIN rom_agg ra ON ra.DatGameId = dg.Id
    LEFT JOIN disk_agg da ON da.DatGameId = dg.Id
    WHERE COALESCE(ra.rom_count, 0) > 0
       OR COALESCE(da.disk_count, 0) > 0
)
SELECT
    'expected_catalog_by_bucket' AS report,
    platform_short_name,
    platform_name,
    media_category,
    size_bucket,
    recommended_delivery,
    COUNT(*) AS game_count,
    ROUND(100.0 * COUNT(*) / SUM(COUNT(*)) OVER (PARTITION BY platform_short_name), 1) AS pct_of_platform,
    ROUND(AVG(payload_bytes) / 1048576.0, 1) AS avg_mib,
    ROUND(MAX(payload_bytes) / 1048576.0, 1) AS max_mib
FROM game_payloads
GROUP BY
    platform_short_name,
    platform_name,
    media_category,
    size_bucket,
    recommended_delivery
ORDER BY platform_short_name, MIN(payload_bytes);

WITH
rom_agg AS (
    SELECT
        dr.DatGameId,
        COUNT(*) AS rom_count,
        SUM(dr.Size) AS payload_bytes,
        MAX(CASE
            WHEN lower(dr.Name) GLOB '*.cue'
              OR lower(dr.Name) GLOB '*.iso'
              OR lower(dr.Name) GLOB '*.chd'
              OR lower(dr.Name) GLOB '*.gdi'
              OR lower(dr.Name) GLOB '*.cdi'
              OR lower(dr.Name) GLOB '*.rvz'
              OR lower(dr.Name) GLOB '*.wbfs'
              OR lower(dr.Name) GLOB '*.wud'
              OR lower(dr.Name) GLOB '*.xiso'
            THEN 1 ELSE 0
        END) AS has_large_media_extension
    FROM DatRoms dr
    GROUP BY dr.DatGameId
),
disk_agg AS (
    SELECT dd.DatGameId, COUNT(*) AS disk_count
    FROM DatDisks dd
    GROUP BY dd.DatGameId
),
game_payloads AS (
    SELECT
        COALESCE(p.ShortName, '(unassigned)') AS platform_short_name,
        COALESCE(p.Name, '(unassigned)') AS platform_name,
        CASE
            WHEN COALESCE(da.disk_count, 0) > 0 THEN 'disk_manifest_no_size'
            WHEN COALESCE(ra.has_large_media_extension, 0) = 1 THEN 'optical_or_large_media'
            WHEN p.ShortName IN (
                'psx', 'ps2', 'ps3', 'ps4', 'ps5', 'psp', 'vita',
                'segacd', 'saturn', 'dc', 'gc', 'wii', 'wiiu', 'switch',
                'xbox', 'x360', 'xone', 'xsx',
                'tgcd', 'neogeocd', '3do', 'cdi'
            ) THEN 'optical_or_large_media'
            WHEN p.ShortName IN ('arcade', 'c64', 'amiga', 'atarist', 'dos') THEN 'mixed_or_unknown'
            ELSE 'cartridge_or_small_media'
        END AS media_category,
        COALESCE(ra.payload_bytes, 0) AS payload_bytes,
        COALESCE(da.disk_count, 0) AS disk_count
    FROM DatGames dg
    JOIN DatFiles df ON df.Id = dg.DatFileId
    LEFT JOIN Platforms p ON p.Id = df.PlatformId
    LEFT JOIN rom_agg ra ON ra.DatGameId = dg.Id
    LEFT JOIN disk_agg da ON da.DatGameId = dg.Id
    WHERE COALESCE(ra.rom_count, 0) > 0
       OR COALESCE(da.disk_count, 0) > 0
)
SELECT
    'expected_catalog_threshold_coverage' AS report,
    platform_short_name,
    platform_name,
    media_category,
    COUNT(*) AS game_count,
    ROUND(100.0 * SUM(CASE WHEN disk_count = 0 AND payload_bytes <= 16 * 1024 * 1024 THEN 1 ELSE 0 END) / COUNT(*), 1) AS pct_le_16_mib,
    ROUND(100.0 * SUM(CASE WHEN disk_count = 0 AND payload_bytes <= 64 * 1024 * 1024 THEN 1 ELSE 0 END) / COUNT(*), 1) AS pct_le_64_mib,
    ROUND(100.0 * SUM(CASE WHEN disk_count = 0 AND payload_bytes <= 128 * 1024 * 1024 THEN 1 ELSE 0 END) / COUNT(*), 1) AS pct_le_128_mib,
    ROUND(100.0 * SUM(CASE WHEN disk_count = 0 AND payload_bytes <= 256 * 1024 * 1024 THEN 1 ELSE 0 END) / COUNT(*), 1) AS pct_le_256_mib,
    ROUND(100.0 * SUM(CASE WHEN disk_count = 0 AND payload_bytes <= 1024 * 1024 * 1024 THEN 1 ELSE 0 END) / COUNT(*), 1) AS pct_le_1_gib,
    SUM(CASE WHEN disk_count > 0 THEN 1 ELSE 0 END) AS disk_unknown_size_count
FROM game_payloads
GROUP BY platform_short_name, platform_name, media_category
ORDER BY platform_short_name, media_category;

WITH
matched_rom_files AS (
    SELECT DISTINCT
        rf.Id AS rom_file_id,
        COALESCE(p.ShortName, '(unassigned)') AS platform_short_name,
        COALESCE(p.Name, '(unassigned)') AS platform_name,
        f.Size AS size_bytes,
        f.SizeOnDisk AS size_on_disk_bytes
    FROM RomFiles rf
    JOIN Files f ON f.Id = rf.FileId
    JOIN DatRoms dr ON dr.RomFileId = rf.Id
    JOIN DatGames dg ON dg.Id = dr.DatGameId
    JOIN DatFiles df ON df.Id = dg.DatFileId
    LEFT JOIN Platforms p ON p.Id = df.PlatformId
),
unmatched_rom_files AS (
    SELECT
        rf.Id AS rom_file_id,
        '(unmatched)' AS platform_short_name,
        '(unmatched)' AS platform_name,
        f.Size AS size_bytes,
        f.SizeOnDisk AS size_on_disk_bytes
    FROM RomFiles rf
    JOIN Files f ON f.Id = rf.FileId
    WHERE NOT EXISTS (
        SELECT 1
        FROM DatRoms dr
        WHERE dr.RomFileId = rf.Id
    )
),
physical_files AS (
    SELECT * FROM matched_rom_files
    UNION ALL
    SELECT * FROM unmatched_rom_files
)
SELECT
    'ingested_physical_files_by_bucket' AS report,
    platform_short_name,
    platform_name,
    CASE
        WHEN size_bytes <= 16 * 1024 * 1024 THEN '<=16 MiB'
        WHEN size_bytes <= 64 * 1024 * 1024 THEN '>16-64 MiB'
        WHEN size_bytes <= 128 * 1024 * 1024 THEN '>64-128 MiB'
        WHEN size_bytes <= 256 * 1024 * 1024 THEN '>128-256 MiB'
        WHEN size_bytes <= 1024 * 1024 * 1024 THEN '>256 MiB-1 GiB'
        ELSE '>1 GiB'
    END AS size_bucket,
    COUNT(DISTINCT rom_file_id) AS file_count,
    ROUND(AVG(size_bytes) / 1048576.0, 1) AS avg_mib,
    ROUND(MAX(size_bytes) / 1048576.0, 1) AS max_mib,
    ROUND(SUM(size_bytes) / 1048576.0, 1) AS total_mib,
    ROUND(SUM(size_on_disk_bytes) / 1048576.0, 1) AS total_on_disk_mib
FROM physical_files
GROUP BY platform_short_name, platform_name, size_bucket
ORDER BY platform_short_name, MIN(size_bytes);

SELECT
    'dat_disks_unknown_size' AS report,
    COALESCE(p.ShortName, '(unassigned)') AS platform_short_name,
    COALESCE(p.Name, '(unassigned)') AS platform_name,
    COUNT(*) AS disk_count,
    COUNT(DISTINCT dg.Id) AS game_count
FROM DatDisks dd
JOIN DatGames dg ON dg.Id = dd.DatGameId
JOIN DatFiles df ON df.Id = dg.DatFileId
LEFT JOIN Platforms p ON p.Id = df.PlatformId
GROUP BY platform_short_name, platform_name
ORDER BY disk_count DESC;
