using System.Text;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Source.Rom;
using Romd.Application.Common.Ids;
using Romd.Consumer.Application.Vfs;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Domain.Vfs;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;
using Romd.Persistence.MediaFacts;

namespace Romd.Media.Probe;

/// <summary>Seeds a private published catalog; subsequent HTTP reads use the real live-library repository.</summary>
internal static class ChdCdReadCatalog
{
    internal sealed record Member(int Disc, int Ordinal, string Kind, string Url, string OraclePath, RomCdFileIdentity Identity);
    internal sealed record User(Guid Id, IssuedVfsCredential Credential);
    internal sealed record Published(IReadOnlyList<Member> Members, IReadOnlyList<User> Users);

    internal static async Task<Published> SeedAsync(RomdDbContext db,
        IReadOnlyList<ChdCdQualificationChecks.QualifiedArchive> archives, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var library = new LibraryEntity { Name = "Read qualification", NeedsMaterialization = false, CreatedAt = now };
        var platform = new PlatformEntity
        {
            Name = "PlayStation",
            ShortName = "psx",
            CanonicalKey = "psx",
            CreatedAt = now,
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "PlayStation",
            BaseCompactLabel = "PlayStation"
        };
        db.AddRange(library, platform);
        await db.SaveChangesAsync(ct);
        var users = Enumerable.Range(0, 3).Select(index => new RomdUser
        {
            Id = Guid.NewGuid(),
            UserName = "read-probe-" + index,
            NormalizedUserName = "READ-PROBE-" + index,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            LibraryId = library.Id,
            CreatedAt = now
        }).ToArray();
        db.Users.AddRange(users);
        await db.SaveChangesAsync(ct);
        var members = new List<Member>();
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var publisher = new MediaFactPublisher(db);
            for (int disc = 0; disc < archives.Count; disc++)
            {
                var archive = archives[disc];
                var manifest = (RomReconstructionManifest.CdTracks)archive.Measured.Manifest;
                var title = new TitleEntity
                {
                    PlatformId = platform.Id,
                    Name = "Disc " + (disc + 1),
                    NormalizedName = "disc " + (disc + 1),
                    EnrichmentStatus = "Completed",
                    CreatedAt = now
                };
                db.Titles.Add(title);
                await db.SaveChangesAsync(ct);
                var release = new CatalogReleaseEntity
                {
                    PlatformId = platform.Id,
                    CatalogTitleId = title.Id,
                    Fingerprint = archive.Measured.Measurement.ArtifactSha256.ToString(),
                    Name = title.Name,
                    FileCount = manifest.Members.Count,
                    SizeBytes = manifest.Members.Sum(member => member.Identity.Size),
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.CatalogReleases.Add(release);
                await db.SaveChangesAsync(ct);
                db.MaterializedLibraryReleases.Add(new MaterializedLibraryReleaseEntity
                {
                    LibraryId = library.Id,
                    TitleId = title.Id,
                    PlatformId = platform.Id,
                    CatalogReleaseId = release.Id,
                    DatGameId = disc + 1,
                    DatFileId = 1,
                    IsOwned = true,
                    IsExposed = true,
                    IsComplete = true,
                    IsEligible = true
                });
                for (int ordinal = 0; ordinal < manifest.Members.Count; ordinal++)
                {
                    var member = manifest.Members[ordinal];
                    string original = Path.Combine(archive.Layout.Input.FixtureDirectory, member.Path);
                    if (member is RomCdStoredMember)
                        await PublishDescriptorAsync(db, publisher, member, original, now, ct);
                    db.CatalogReleaseFiles.Add(new CatalogReleaseFileEntity
                    {
                        CatalogReleaseId = release.Id,
                        FileFingerprint = "member-" + ordinal,
                        Name = member.Path,
                        Size = member.Identity.Size,
                        Sha1 = member.Identity.Sha1,
                        Md5 = member.Identity.Md5,
                        Crc = member.Identity.Crc32
                    });
                    if (member is RomCdBinMember bin)
                    {
                        var track = bin.Tracks.Single();
                        string kind = manifest.Tracks.Single(candidate => candidate.Number == track).Kind.ToString();
                        string path = IdCoder.Encode(platform.Id) + " " + platform.Name + "/" + IdCoder.Encode(release.Id) + " " + release.Name + "/" + member.Path;
                        members.Add(new(disc + 1, ordinal, kind, "/vfs/library/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString)), original, member.Identity));
                    }
                }
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        var issued = new List<User>();
        foreach (var user in users)
        {
            await using var transaction = await new EfUnitOfWork(db).BeginTransactionAsync(ct);
            var credential = await new VfsConnectionStore(db, TimeProvider.System).CreateAsync(user.Id, "Read qualification", ct, VfsFormat.Original);
            Check.That(!credential.IsError, "real VFS connection issuance");
            await transaction.CommitAsync(ct);
            issued.Add(new(user.Id, credential.Value));
        }
        return new(members, issued);
    }

    private static async Task PublishDescriptorAsync(RomdDbContext db, MediaFactPublisher publisher,
        RomCdFileMember member, string path, DateTimeOffset now, CancellationToken ct)
    {
        var measured = await Digests.FileAsync(path, ct);
        var identity = member.Identity;
        Check.That(measured.Size == identity.Size && measured.Sha256 == identity.Sha256.ToString() &&
            measured.Sha1 == identity.Sha1.ToString() && measured.Md5 == identity.Md5.ToString() && measured.Crc == identity.Crc32.ToString(),
            "independently rehashed exact retained descriptor");
        var file = await db.Files.SingleAsync(file => file.Sha256 == identity.Sha256 && file.Size == identity.Size, ct);
        var logical = new RomLogicalIdentity(identity.Size, identity.Sha256, identity.Sha1, identity.Md5, identity.Crc32);
        int rom = await publisher.CreateLogicalAsync(member.Path, logical, Guid.Empty, now, ct);
        string build = "romd-read-probe/mvid:" + typeof(ChdCdReadCatalog).Module.ModuleVersionId;
        var proof = new RomRepresentationVerification(Guid.NewGuid(), identity.Sha256, identity.Size,
            new RomDecoderIdentity.Managed(build), "identity/v1", build, now,
            [new(RomOutputSelector.Whole.Instance, logical)], RomContainerFacts.Identity.Instance);
        await publisher.PublishAsync(file.Id, RomContainerFacts.Identity.Instance, RomReconstructionManifest.WholeFile.Instance,
            proof, [new(RomOutputSelector.Whole.Instance, rom, true)], [], ct);
    }
}
