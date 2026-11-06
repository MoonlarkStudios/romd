using System.Buffers.Binary;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Compressed;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Hashing;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Storage;
using Romd.Media.PlayStation;
using Romd.Persistence;
using Romd.Persistence.Catalog;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Repositories;
using Romd.Persistence.Repositories.Reconciliation;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;

namespace Romd.Infrastructure.Tests.Storage;

internal static partial class MediaInfrastructureTestServices
{
    // Synthetic trusted-port observations exercise ownership/transaction policy. Actual native qualification stays in supervised tests.
    internal sealed class CdImportInput(ITempFile source, RomCdInspection inspection, byte[] cueBytes, IReadOnlyList<string> binNames) : IAsyncDisposable
    {
        internal PreparedRom.Cd Prepared { get; } = new(source, inspection);
        internal byte[] CueBytes { get; } = cueBytes;
        internal IReadOnlyList<string> BinNames { get; } = Array.AsReadOnly(binNames.ToArray());
        public ValueTask DisposeAsync() => source.DisposeAsync();
    }

    internal static async Task<CdImportInput> MeasuredCdInputAsync(LocalTempFileFactory files, RomCdStorageRecipe recipe,
        RomChdDecoderBuild decoder, bool canonical, byte artifactVariant, int trackCount, RomPs1CdBoot? boot)
    {
        if (trackCount is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(trackCount));
        uint logicalBytes = checked((uint)trackCount * 64 * 2448);
        uint hunk = canonical ? recipe.HunkBytes : 2448;
        uint[] codecs = canonical ? recipe.Codecs.ToArray() : [0x63646c7a, 0, 0, 0];
        var raw = Sha1.Parse(new string('e', 40));
        var overall = Sha1.Parse(new string('f', 40));
        byte[] bytes = new byte[512];
        ChdV5Prefix(2448).CopyTo(bytes, 0);
        for (int index = 0; index < codecs.Length; index++)
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16 + index * 4), codecs[index]);
        raw.AsSpan().CopyTo(bytes.AsSpan(64));
        overall.AsSpan().CopyTo(bytes.AsSpan(84));
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(32), logicalBytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(56), hunk);
        bytes[^1] = artifactVariant;
        var source = await files.CreateAsync(new MemoryStream(bytes), includeDatHashes: _ => true);
        try
        {
            var metadata = Enumerable.Range(1, trackCount).Select(number => new RomChdMetadataEntry(0x43485432, 1,
                Encoding.ASCII.GetBytes($"TRACK:{number} TYPE:{(number == 1 ? "MODE2_RAW" : "AUDIO")} SUBTYPE:NONE FRAMES:64 PREGAP:0 PGTYPE:{(number == 1 ? "MODE2_RAW" : "AUDIO")} PGSUB:NONE POSTGAP:0" + (char)0))).ToArray();
            var tracks = Enumerable.Range(1, trackCount).Select(number => new RomCdInspectedTrack(
                new(number, (uint)((number - 1) * 64), 64, number == 1 ? RomCdTrackKind.Mode2Raw : RomCdTrackKind.Audio), 0, 0, false, 0,
                MediaFactFixtures.Identity(Enumerable.Repeat((byte)(6 + number), 64 * 2352).ToArray()))).ToArray();
            var names = Enumerable.Range(1, trackCount).Select(number => number == 1 ? "track.bin" : $"track-{number:00}.bin").ToArray();
            var container = MediaFactFixtures.Chd(logicalBytes, hunk, 2448, codecs, raw, overall, metadata);
            var observed = new RomCdInspection(Guid.NewGuid(), MediaFactFixtures.Identity(bytes), container, decoder, ChdCdInspectionService.Profile,
                ChdCdInspectionService.ProjectionProfile, "fixture-verifier", DateTimeOffset.UnixEpoch.AddDays(1), raw, overall, tracks, boot);
            var generated = await new RomCdCueGenerator(files).GenerateAsync(observed.Tracks, names);
            if (generated.IsError)
                throw new InvalidDataException(string.Join(';', generated.Errors));
            await using var cue = generated.Value;
            return new(source, observed, await File.ReadAllBytesAsync(cue.Artifact.Path), names);
        }
        catch { await source.DisposeAsync(); throw; }
    }

    internal static async Task<RomLogicalIdentity> SeedCdCatalogAsync(RomdDbContext db, CdImportInput input, bool exactCue)
    {
        var expected = MediaFactFixtures.Identity(exactCue ? input.CueBytes : Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(input.CueBytes).Replace("\r\n", "\n", StringComparison.Ordinal)));
        db.Platforms.Add(new()
        {
            Id = 1,
            Name = "Synthetic PlayStation",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "Synthetic PlayStation",
            BaseCompactLabel = "PS",
            ShortName = "psx",
            CanonicalKey = "psx"
        });
        db.Titles.Add(new() { Id = 1, PlatformId = 1, Name = "Synthetic disc", NormalizedName = "synthetic disc", EnrichmentStatus = "Completed" });
        db.CatalogReleases.Add(new() { Id = 1, PlatformId = 1, CatalogTitleId = 1, Fingerprint = "sha1:synthetic-cd", Name = "Synthetic disc" });
        for (int index = 0; index < input.Prepared.Inspection.Tracks.Count; index++)
        {
            var bin = input.Prepared.Inspection.Tracks[index].Identity;
            db.CatalogReleaseFiles.Add(new()
            {
                Id = index == 0 ? 1 : index + 2,
                CatalogReleaseId = 1,
                FileFingerprint = $"bin-{index + 1}-v1",
                Name = input.BinNames[index],
                Size = bin.Size,
                Sha1 = bin.Sha1,
                Md5 = bin.Md5,
                Crc = bin.Crc32
            });
        }
        db.CatalogReleaseFiles.Add(new()
        {
            Id = 2,
            CatalogReleaseId = 1,
            FileFingerprint = "cue-v1",
            Name = "disc.cue",
            Size = expected.Size,
            Sha1 = expected.Sha1,
            Md5 = expected.Md5,
            Crc = expected.Crc32
        });
        await db.SaveChangesAsync();
        return expected;
    }

    internal sealed record CdImportServices(IRomCdIngestor Ingestor, IRomCdImportStore Store, FileStorageService Files,
        ITrackedTitleRepository Tracking, EfUnitOfWork Work);

    internal static CdImportServices CdImport(RomdDbContext db, IContentAddressableStore cas, LocalTempFileFactory temp,
        RomCdStorageRecipe recipe, IAdminEventOutbox applicationOutbox, bool tracked, IRomCdCueGenerator generator)
    {
        var work = new EfUnitOfWork(db);
        var locks = new FileMutationLock(db);
        var facts = new MediaFactPublisher(db);
        var files = CreateFiles(db, cas, work, locks, TimeProvider.System);
        var store = new RomCdImportStore(db, facts, new CatalogCdLayoutStore(db), locks);
        var trackedTitles = Substitute.For<ITrackedTitleReader>();
        trackedTitles.HasTrackedAsync(Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>()).Returns(tracked);
        var tracking = Substitute.For<ITrackedTitleRepository>();
        tracking.TrackByTitleIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns(_ =>
        { if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Tracking escaped the content transaction."); return 1; });
        var impact = Substitute.For<IRomPayloadAssertionImpactReader>();
        impact.ReadSourceEntryIdsBySha1Async(Arg.Any<Sha1>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
        var publisher = new RomCdArchivePublisher(files, work, facts, new JobItemRepository(db));
        var publication = new RomCdImportPublication(publisher, store, recipe, trackedTitles, tracking, Substitute.For<ILibraryRepository>(), impact,
            Substitute.For<ITitlePayloadAvailabilityProjection>(), applicationOutbox, new MediaReconciliationRequests(db, TimeProvider.System), new JobAuditContext(), TimeProvider.System);
        var preparation = ManagedPreparation(new HashingService(), () => throw new InvalidOperationException("CUE bytes must not invoke an encoder or RVZ verifier."), new EmptyReferences());
        return new(new RomCdIngestor(new RomCdImportCatalogReader(db), generator, publication, recipe, preparation), store, files, tracking, work);
    }
}
