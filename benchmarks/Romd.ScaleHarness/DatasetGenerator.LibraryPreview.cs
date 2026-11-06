using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Romd.Domain.Hashing;
using Romd.Admin.Application.Titles.Matching;
using Romd.Persistence.Search;
using Romd.Domain.Source.Rom;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;

namespace Romd.ScaleHarness;

public sealed partial class DatasetGenerator
{
    // This separate fixture uses the current media baseline. The older admin spike's retired
    // RomFiles loader is deliberately not used or repaired by the preview benchmark.
    private async Task<DatasetManifest> GenerateLibraryPreviewAsync(Stopwatch stopwatch, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_options.ScaleConnectionString);
        await connection.OpenAsync(ct);
        await InsertPreviewTaxonomyAsync(ct);
        await InsertPreviewOriginalsAsync(ct);
        await InsertPreviewTitlesAsync(connection, ct);
        await InsertTrackedTitlesAsync(connection, ct);
        await using (var ratings = new BulkInserter(connection, "TitleContentRatings",
            ["Id", "TitleId", "Board", "Code", "Designation", "MinimumAge", "DescriptorsJson", "SourceId", "CreatedAt", "CreatedByUserId"]))
        {
            for (int t = 0; t < Scale.Titles; t++)
                if (_data.TitleEnriched(t))
                    await ratings.AddAsync([t + 1, t + 1, _data.RatingBoardOfTitle(t), _data.RatingCodeOfTitle(t), 0,
                        _data.RatingAgeOfTitle(t), "[]", "igdb", DeterministicDataset.BaseTime.ToUnixTimeMilliseconds(),
                        DeterministicDataset.SystemUserId], ct);
        }
        await using var command = new NpgsqlCommand("""
            INSERT INTO romd."CatalogSources" ("Id","Kind","Status","CreatedAt","CreatedByUserId")
            SELECT s,'Dat','Active',@now,@actor FROM generate_series(1,16) s;
            INSERT INTO romd."DatSources" ("Id","CatalogSourceId","CreatedAt","CreatedByUserId")
            SELECT s,s,@now,@actor FROM generate_series(1,16) s;
            INSERT INTO romd."DatFiles" ("Id","Name","Description","Type","OriginalFilename","FileId","PlatformId","DatSourceId","GameCount","RomCount","DiskCount","Lifecycle","CreatedAt","CreatedByUserId")
            SELECT s,'Preview source '||s,'Synthetic duplicate release assertions','NoIntro','source'||s||'.dat',33,
                   (s+1)/2,s,0,0,0,'Active',@now,@actor FROM generate_series(1,16) s;
            UPDATE romd."Libraries" SET "ConfigurationJson"=@config;
            INSERT INTO romd."CatalogReleases" ("Id","PlatformId","CatalogTitleId","Fingerprint","Name","SourceCount","HasTitleConflict","SizeBytes","FileCount","CreatedAt","CreatedByUserId","UpdatedAt")
            SELECT "Id","PlatformId","Id",'preview-'||"Id","Name",2,false,32,2,@now,@actor,@now
            FROM romd."Titles" WHERE ("Id"-1)%10<>9;
            INSERT INTO romd."SourceEntries" ("Id","CatalogSourceId","EntryKey","Name","PlatformId","HasLocalPayload","CreatedAt","CreatedByUserId")
            SELECT (t."Id"-1)*2+s,(t."PlatformId"-1)*2+s,'title-'||t."Id",t."Name",t."PlatformId",
                   s=1 AND (t."Id"-1)%10<3,@now,@actor
            FROM romd."Titles" t CROSS JOIN generate_series(1,2) s WHERE (t."Id"-1)%10<>9;
            INSERT INTO romd."DatGames" ("Id","DatFileId","SourceEntryId","Name","IsBios","CreatedAt","CreatedByUserId","SearchDocument")
            SELECT e."Id",e."CatalogSourceId",e."Id",e."Name",false,@now,@actor,t."SearchDocument"
            FROM romd."SourceEntries" e JOIN romd."Titles" t ON t."Id"=(e."Id"+1)/2;
            INSERT INTO romd."TitleSourceLinks" ("SourceEntryId","TitleId","CreatedAt","CreatedByUserId")
            SELECT "Id",("Id"+1)/2,@now,@actor FROM romd."SourceEntries";
            INSERT INTO romd."CatalogReleaseSources" ("CatalogReleaseId","SourceEntryId","ProviderClaimKey","AssertedTitleId")
            SELECT ("Id"+1)/2,"Id","Id"::text,("Id"+1)/2 FROM romd."SourceEntries";
            INSERT INTO romd."DatRoms" ("Id","DatGameId","Name","Size","Crc","Md5","Sha1","RomFileId","CreatedAt","CreatedByUserId")
            SELECT (g."Id"-1)*2+slot,g."Id",'part-'||slot||'.bin',r."ContentSize",r."Crc32",r."Md5",r."Sha1",
                   CASE WHEN g."Id"%2=1 AND ((g."Id"-1)/2)%10<3 THEN r."Id" ELSE NULL END,@now,@actor
            FROM romd."DatGames" g CROSS JOIN generate_series(1,2) slot
            JOIN romd."Roms" r ON r."Id"=((g."Id"-1)/2)%16+1+(slot-1)*16;
            INSERT INTO romd."CatalogReleaseFiles" ("Id","CatalogReleaseId","FileFingerprint","Name","Size","Crc","Md5","Sha1","IsDisk")
            SELECT (c."Id"-1)*2+slot,c."Id",'sha1:'||encode(r."Sha1",'hex'),'part-'||slot||'.bin',r."ContentSize",r."Crc32",r."Md5",r."Sha1",false
            FROM romd."CatalogReleases" c CROSS JOIN generate_series(1,2) slot
            JOIN romd."Roms" r ON r."Id"=(c."Id"-1)%16+1+(slot-1)*16;
            INSERT INTO romd."CatalogReleaseFileSources" ("CatalogReleaseFileId","SourceEntryId","ProviderClaimKey","ProviderRequirementKey","RequirementKind")
            SELECT (((g."Id"+1)/2)-1)*2+slot,g."SourceEntryId",g."Id"::text,((g."Id"-1)*2+slot)::text,'Rom'
            FROM romd."DatGames" g CROSS JOIN generate_series(1,2) slot;
            INSERT INTO romd."DatGameRegions" ("DatGameId","RegionId","CreatedAt","CreatedByUserId") SELECT "Id",("Id"+1)/2%10+1,@now,@actor FROM romd."DatGames";
            INSERT INTO romd."DatGameLanguages" ("DatGameId","GameLanguageId","CreatedAt","CreatedByUserId") SELECT "Id",("Id"+1)/2%10+1,@now,@actor FROM romd."DatGames";
            UPDATE romd."DatFiles" d SET "GameCount"=(SELECT count(*) FROM romd."DatGames" g WHERE g."DatFileId"=d."Id"),
                "RomCount"=2*(SELECT count(*) FROM romd."DatGames" g WHERE g."DatFileId"=d."Id");
            ANALYZE;
            """, connection);
        command.Parameters.AddWithValue("now", DeterministicDataset.BaseTime.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("actor", DeterministicDataset.SystemUserId);
        command.Parameters.AddWithValue("config", JsonSerializer.Serialize(LibraryPreviewDataset.SavedConfiguration));
        await command.ExecuteNonQueryAsync(ct);

        foreach (string table in new[] { "Titles", "TrackedTitles", "TitleContentRatings", "DatFiles", "SourceEntries", "DatGames",
            "DatRoms", "CatalogReleases", "CatalogReleaseSources", "CatalogReleaseFiles", "CatalogReleaseFileSources",
            "DatGameRegions", "DatGameLanguages", "Roms", "Representations", "RepresentationOutputs", "Verifications" })
            _rowCounts[table] = await ScalarAsync(connection, $"SELECT count(*) FROM romd.\"{table}\"", ct);
        long linked = Enumerable.Range(0, Scale.Titles).Count(LibraryPreviewDataset.HasCandidates);
        if (_rowCounts["Titles"] != Scale.Titles || _rowCounts["CatalogReleases"] != linked ||
            _rowCounts["DatGames"] != linked * 2 || _rowCounts["DatRoms"] != linked * 4)
            throw new InvalidOperationException("Preview fixture row counts differ from the deterministic shape.");
        if (await ScalarAsync(connection, """
            SELECT count(*) FROM romd."DatRoms" d JOIN romd."Roms" r ON r."Id"=d."RomFileId"
            LEFT JOIN romd."RepresentationOutputs" o ON o."Id"=r."PreferredOutputId" AND o."RomId"=r."Id"
            WHERE o."Id" IS NULL OR d."Sha1"<>r."Sha1" OR d."Size"<>r."ContentSize"
            """, ct) != 0)
            throw new InvalidOperationException("Preview owned requirements lack complete matching original facts.");
        stopwatch.Stop();
        var manifest = new DatasetManifest(ScaleParameters.SpecVersion, Scale.Name, ScaleParameters.Seed,
            _options.DatabaseName, DateTimeOffset.UtcNow, stopwatch.Elapsed.TotalSeconds, _data.JobId(0),
            _data.LargestPlatformId, DeterministicDataset.SearchToken, _data.ExpectedSagaTitleCount(), _rowCounts,
            ["Preview fixture: 10% candidate-free; two duplicate sources per canonical release, two ROM requirements per source; " +
             "30% titles own the complete first-source release; 32 shared measured original identities; 40% rated; two thirds tracked."],
            _options.DatasetKind);
        await manifest.WriteAsync(_options.ManifestPath, ct);
        return manifest;
    }

    private async Task InsertPreviewTaxonomyAsync(CancellationToken ct)
    {
        await using var provider = HarnessServices.Build(_options.ScaleConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        for (int id = 1; id <= ScaleParameters.PlatformCount; id++)
            db.Platforms.Add(new PlatformEntity
            {
                Id = id,
                Name = $"Platform {id:D2}",
                ShortName = $"plat{id:D2}",
                CreatedAt = DeterministicDataset.BaseTime
            });
        for (int id = 1; id <= ScaleParameters.RegionCount; id++)
            db.Regions.Add(new RegionEntity
            {
                Id = id,
                Name = DeterministicDataset.RegionName(id),
                CanonicalKey = UnregisteredKey(DeterministicDataset.RegionName(id)),
                SortOrder = id,
                CreatedAt = DeterministicDataset.BaseTime
            });
        for (int id = 1; id <= ScaleParameters.LanguageCount; id++)
            db.GameLanguages.Add(new GameLanguageEntity
            {
                Id = id,
                Name = DeterministicDataset.LanguageName(id),
                CanonicalKey = UnregisteredKey(DeterministicDataset.LanguageName(id)),
                Code = $"l{id}",
                SortOrder = id,
                CreatedAt = DeterministicDataset.BaseTime
            });
        db.Libraries.Add(new LibraryEntity
        {
            Id = 1,
            Name = "Preview baseline",
            ConfigurationJson = JsonSerializer.Serialize(LibraryPreviewDataset.SavedConfiguration),
            CreatedAt = DeterministicDataset.BaseTime
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task InsertPreviewTitlesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var titles = new BulkInserter(connection, "Titles",
            ["Id", "Revision", "PlatformId", "Name", "NormalizedName", "Genre", "SearchDocument", "EnrichmentStatus",
             "FieldProvenanceJson", "FieldSourceOverridesJson", "ScreenshotPrefsJson", "CreatedAt", "CreatedByUserId",
             "RetainWithoutCatalog", "HasLocalPayload"]);
        for (int t = 0; t < Scale.Titles; t++)
        {
            string name = _data.TitleName(t);
            await titles.AddAsync([t + 1, new Guid(t + 1, 0, 0, new byte[8]), _data.PlatformOfTitle(t), name,
                TitleNormalizer.Normalize(name), _data.TitleGenre(t), SearchDocuments.For(name, null), "None", "{}", "{}", "{}",
                DeterministicDataset.BaseTime.ToUnixTimeMilliseconds(), DeterministicDataset.SystemUserId, false,
                LibraryPreviewDataset.IsOwned(t)], ct);
        }
    }

    private async Task InsertPreviewOriginalsAsync(CancellationToken ct)
    {
        await using var provider = HarnessServices.Build(_options.ScaleConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var publisher = new MediaFactPublisher(db);
        for (int id = 1; id <= 33; id++)
        {
            byte[] bytes = new byte[16];
            bytes[0] = (byte)id;
            var identity = new RomLogicalIdentity(bytes.Length, Sha256.FromSpan(SHA256.HashData(bytes)),
                Sha1.FromSpan(SHA1.HashData(bytes)), Md5.FromSpan(MD5.HashData(bytes)),
                Crc32.FromUInt32(System.IO.Hashing.Crc32.HashToUInt32(bytes)));
            db.Files.Add(new FileEntityPersistence
            {
                Id = id,
                Sha256 = identity.Sha256,
                Size = identity.Size,
                SizeOnDisk = identity.Size,
                CreatedAt = DeterministicDataset.BaseTime,
                CreatedByUserId = DeterministicDataset.SystemUserId
            });
            await db.SaveChangesAsync(ct);
            if (id == 33)
                continue; // Shared DAT file, never a logical ROM.
            int romId = await publisher.CreateLogicalAsync($"part-{id}.bin", identity,
                DeterministicDataset.SystemUserId, DeterministicDataset.BaseTime, ct);
            if (romId != id)
                throw new InvalidOperationException("Fresh preview logical ROM ids must be deterministic.");
            var container = RomContainerFacts.Identity.Instance;
            var measurement = new RomRepresentationVerification(Guid.NewGuid(), identity.Sha256, identity.Size,
                new RomDecoderIdentity.Managed("scale-preview-original/v1"), "identity/v1", "scale-preview-original/v1",
                DeterministicDataset.BaseTime, [new(RomOutputSelector.Whole.Instance, identity)], container);
            await publisher.PublishAsync(id, container, RomReconstructionManifest.WholeFile.Instance, measurement,
                [new(RomOutputSelector.Whole.Instance, romId, true)], [], ct);
        }
        await transaction.CommitAsync(ct);
    }
}
