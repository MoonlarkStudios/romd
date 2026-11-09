using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Source.Rom;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media.Cd;
using Romd.Media.Processes;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;

namespace Romd.Media.Probe;

/// <summary>Publishes independently qualified CDs into a fresh private catalog/CAS, then measures real member decoding through VFS.</summary>
internal static class ChdCdVfsReadChecks
{
    internal static async Task RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length != 5 || !int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) ||
            seconds is < 1800 or > 86400)
            throw new ArgumentException("Usage: --chd-cd-vfs-load-probe <absolute qualification.json> <absolute new qualification-report.json> <absolute new load-report.json> <seconds:1800..86400>");
        MediaProbePaths.Validate([args[1], args[2], args[3]]);
        Check.That(Path.GetFullPath(args[2]) != Path.GetFullPath(args[3]) && !File.Exists(args[3]),
            "qualification and read-load reports are distinct fresh files");
        var qualified = await ChdCdQualificationChecks.RunAsync(["--chd-cd-qualification-probe", args[1], args[2]],
            ChdCdQualificationChecks.Retention.ArchiveForReadLoad, [args[3]], ct);
        Check.That(qualified.Archives.Count > 1, "read capacity covers multiple independently qualified real CD inputs");
        var config = qualified.Configuration;
        var media = RomMediaPolicyComposition.Create();
        var cd = media.Kinds.Kinds.Single(kind => kind.ManifestType == MediaManifestType.CdTracks);
        Check.That(cd.PlatformKeys.Count == 1, "the single qualified CD kind has an exact platform binding");
        var read = CreateReadComposition(config.NativeDirectory, media, ct);
        await using var ownedRead = read.Decoder;
        await using var database = PostgreSqlTestDatabase.Create();
        await using var services = new ServiceCollection().AddContentAddressableStorage(storage =>
        {
            storage.RootPath = Path.Combine(config.DataDirectory, "read-load-cas");
            storage.WritePolicy = ContentWritePolicy.Raw;
            storage.MaxContentSize = ChdCdLimits.MaximumArtifactBytes;
        }).BuildServiceProvider();
        var cas = services.GetRequiredService<IContentAddressableStore>();
        IReadOnlyList<(int RomId, string OraclePath)> members;
        await using (var db = database.CreateContext())
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            members = await PublishReadLibraryAsync(db, cas, qualified.Archives, ct);
            await transaction.CommitAsync(ct);
        }
        var catalog = new ProbeCatalog(database.CreateOptions());
        var library = new List<(RomContentDescriptor Content, string IsoPath, string SystemKey)>();
        foreach (var member in members)
        {
            var content = await catalog.FindByRomIdAsync(member.RomId, ct)
                ?? throw new InvalidOperationException("Published CD member disappeared before read qualification.");
            Check.That(content.Artifacts.Count == 1 && content.Encoding == RomRepresentationEncoding.Chd,
                "every logical BIN is fully converged with no original CAS representation");
            library.Add((content, member.OraclePath, cd.PlatformKeys.Single()));
        }
        var staging = new TempStorageOptions();
        var scope = new ReadResourceChecks.StagingScope(Path.Combine(config.DataDirectory, "temp", "staging-v1"),
            staging.MaximumFiles, staging.MaximumBytes, ChdCdLimits.MaximumArtifactBytes, staging.MinimumFreeBytes);
        await ConvergedReadChecks.RunAsync(cas, catalog, library, media, read.Admission, read.Decoder, ConvergedReadProfile.CdMembers,
            TimeSpan.FromSeconds(seconds), args[3], ct, scope);
    }

    internal static (IRomChdDeliveryAdmission Admission, ConvergedReadDecoder Decoder) CreateReadComposition(
        string nativeDirectory, RomMediaComposition media, CancellationToken ct)
    {
        var runtime = new ChdDeploymentRuntime(new NativeToolOptions { LibchdrDirectory = nativeDirectory }, requireTool: false);
        runtime.Verify(ct);
        var native = runtime.Assets?.Native ?? throw new InvalidOperationException("Qualified CD reads require their verified native candidate.");
        return (new ChdDeliveryAdmission(runtime, new MediaHostCapabilities(runtime, media, encodes: false), media.Kinds),
            ConvergedReadDecoder.NativeChd(native));
    }

    internal static async Task<IReadOnlyList<(int RomId, string OraclePath)>> PublishReadLibraryAsync(
        RomdDbContext db, IContentAddressableStore cas, IReadOnlyList<ChdCdQualificationChecks.QualifiedArchive> archives, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Read library publication requires its caller transaction.");
        var members = new List<(int RomId, string OraclePath)>();
        var publisher = new MediaFactPublisher(db);
        foreach (var archive in archives)
        {
            var stored = await StoreAsync(cas, archive.ArtifactPath, ct);
            Check.That(stored.Key.Hash == archive.Measured.Measurement.ArtifactSha256 && stored.Size == archive.Measured.Measurement.ArtifactSize &&
                !stored.IsCompressed, "CAS contains the exact measured raw canonical CHD");
            int fileId = await FileAsync(db, stored, ct);
            var manifest = (RomReconstructionManifest.CdTracks)archive.Measured.Manifest;
            var owners = new List<MediaOutputOwner>();
            var descriptors = new List<RomRetainedDescriptor>();
            for (int ordinal = 0; ordinal < manifest.Members.Count; ordinal++)
            {
                var member = manifest.Members[ordinal];
                string original = Path.Combine(archive.Layout.Input.FixtureDirectory, member.Path);
                if (member is RomCdBinMember)
                {
                    int romId = await publisher.CreateLogicalAsync(member.Path, new RomLogicalIdentity(member.Identity.Size, member.Identity.Sha256, member.Identity.Sha1, member.Identity.Md5, member.Identity.Crc32), Guid.Empty, DateTimeOffset.UnixEpoch, ct);
                    owners.Add(new(new RomOutputSelector.Member(ordinal), romId, true));
                    members.Add((romId, original));
                }
                else
                {
                    var descriptor = await StoreAsync(cas, original, ct);
                    Check.That(descriptor.Key.Hash == member.Identity.Sha256 && descriptor.Size == member.Identity.Size,
                        "separately retained descriptor bytes match the independent measured original");
                    descriptors.Add(new(ordinal, await FileAsync(db, descriptor, ct), member.Identity.Sha256, member.Identity.Size));
                }
            }
            var graph = await publisher.PublishAsync(fileId, archive.Measured.Container, archive.Measured.Manifest,
                archive.Measured.Measurement, owners, descriptors, ct);
            Check.That(graph.Verifications.Count == 1 && graph.Creations.Count == 0,
                "read qualification retains its real native proof without inventing an encoding creation record");
        }
        return members;
    }

    private static async Task<StoreResult> StoreAsync(IContentAddressableStore cas, string path, CancellationToken ct)
    {
        await using var source = new FileStream(NativeFileLease.OpenRegularRead(path), FileAccess.Read);
        await using var staged = await cas.StageRawAsync(source, ct: ct);
        return await staged.PublishAsync(ct);
    }

    private static async Task<int> FileAsync(RomdDbContext db, StoreResult stored, CancellationToken ct)
    {
        var existing = await db.Files.SingleOrDefaultAsync(file => file.Sha256 == stored.Key.Hash, ct);
        if (existing is not null)
        {
            Check.That(existing.Size == stored.Size && existing.SizeOnDisk == stored.StoredSize && !existing.IsCompressed,
                "deduplicated raw file identity remains exact");
            return existing.Id;
        }
        var created = new FileEntityPersistence
        {
            Sha256 = stored.Key.Hash,
            Size = stored.Size,
            SizeOnDisk = stored.StoredSize,
            IsCompressed = stored.IsCompressed,
            CreatedAt = DateTimeOffset.UnixEpoch,
            CreatedByUserId = Guid.Empty
        };
        db.Files.Add(created);
        await db.SaveChangesAsync(ct);
        return created.Id;
    }
}
