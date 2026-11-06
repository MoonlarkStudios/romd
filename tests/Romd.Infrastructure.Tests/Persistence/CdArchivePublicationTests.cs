using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Romd.Admin.Application.Catalog.Commands.DecideOriginalDiscLayout;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Models;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media.PlayStation;
using Romd.Persistence;
using Romd.Persistence.Catalog;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Repositories;
using Romd.Persistence.Repositories.Reconciliation;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

/// <summary>Current publication and consumer contracts over real PostgreSQL graphs; codec observations are explicit synthetic fixtures.</summary>
public sealed partial class CdArchivePublicationTests : IAsyncLifetime
{
    private readonly PostgreSqlTestDatabase database = PostgreSqlTestDatabase.Create();
    private readonly string directory = Directory.CreateTempSubdirectory("romd-cd-publication-").FullName;
    private readonly Guid actor = Guid.NewGuid();
    private static readonly RomChdDecoderBuild Decoder = new("linux-x64", Sha1.Parse(new string('a', 40)), 2);
    private static readonly RomCdStorageRecipe Recipe = RomMediaPolicyComposition.Create().CdRecipe!;
    private readonly Dictionary<string, byte[]> originals = new(StringComparer.Ordinal)
    {
        ["track.bin"] = Enumerable.Range(0, 64 * 2352).Select(i => (byte)(i * 7 + 3)).ToArray(),
        ["audio.bin"] = Enumerable.Range(0, 5 * 2352).Select(i => (byte)(i * 11 + 5)).ToArray(),
        ["disc.cue"] = Encoding.ASCII.GetBytes("FILE \"track.bin\" BINARY\n TRACK 01 MODE2/2352\n INDEX 01 00:00:00\nFILE \"audio.bin\" BINARY\n TRACK 02 AUDIO\n INDEX 01 00:00:00\n")
    };
    // A second region shares audio.bin byte-for-byte, so both releases bind the same logical ROM.
    private readonly byte[] secondTrack = Enumerable.Range(0, 32 * 2352).Select(i => (byte)(i * 13 + 1)).ToArray();
    private readonly byte[] secondCue = Encoding.ASCII.GetBytes("FILE \"b-track.bin\" BINARY\n TRACK 01 MODE2/2352\n INDEX 01 00:00:00\nFILE \"audio.bin\" BINARY\n TRACK 02 AUDIO\n INDEX 01 00:00:00\n");
    public async Task InitializeAsync()
    {
        await using var db = database.CreateContext();
        db.Users.Add(new() { Id = actor, UserName = "cd-test", NormalizedUserName = "CD-TEST", SecurityStamp = Guid.NewGuid().ToString() });
        db.Platforms.Add(new() { Id = 1, Name = "PlayStation", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "PlayStation", BaseCompactLabel = "PS", ShortName = "psx", CanonicalKey = "psx" });
        db.Titles.Add(new() { Id = 1, PlatformId = 1, Name = "Test", NormalizedName = "test", EnrichmentStatus = "Completed" });
        db.CatalogReleases.Add(new() { Id = 1, PlatformId = 1, CatalogTitleId = 1, Fingerprint = "sha1:cd-fixture", Name = "Test" });
        int id = 0;
        foreach (var (path, bytes) in originals)
        {
            id++;
            var hash = Identity(bytes);
            db.Files.Add(new() { Id = id, Sha256 = hash.Sha256, Size = hash.Size, SizeOnDisk = hash.Size });
            if (path.EndsWith("bin"))
            {
                db.StageOriginalRom(new() { Id = id, FileId = id, OriginalFilename = path, ContentSha256 = hash.Sha256, ContentSize = hash.Size, Sha1 = hash.Sha1, Md5 = hash.Md5, Crc32 = hash.Crc32 });
                db.CatalogReleaseFiles.Add(new() { CatalogReleaseId = 1, FileFingerprint = hash.Sha1.ToString(), Name = "catalog-" + path, Size = hash.Size, Sha1 = hash.Sha1 });
            }
        }
        await db.PublishOriginalRomsAsync();
        // A DAT descriptor template is only a reference; it must not replace this original CUE.
        db.CatalogReleaseFiles.Add(new() { CatalogReleaseId = 1, FileFingerprint = "cue-template", Name = "catalog.cue", Size = 17, Sha1 = Identity([1, 2, 3]).Sha1 });
        db.OriginalDiscLayouts.Add(new()
        {
            Id = 1,
            State = "pending",
            SourceJobId = Guid.NewGuid(),
            SourceRelativePath = "disc.cue",
            EntryPoint = "disc.cue",
            DescriptorFileId = 3,
            DependencyPathsJson = "[\"track.bin\",\"audio.bin\"]",
            Revision = Guid.NewGuid(),
            Files = [new() { Path = "track.bin", RomId = 1, FileId = 1 }, new() { Path = "audio.bin", RomId = 2, FileId = 2 }]
        });
        await db.SaveChangesAsync();
    }
    /// <summary>A decode-only host whose verified resident decoder is <see cref="Decoder" /> on <paramref name="rid" />, under the production
    /// media registration unless another is given.</summary>
    private static MediaHostCapabilities Capabilities(string? rid = null, RomMediaComposition? media = null) => new(
        () => new ChdNativeIdentity(rid ?? Decoder.Rid, "libmoonlark_chdr.so", 100, new string('c', 64), Decoder.UpstreamCommit.ToString(), "test-only-upstream",
            Decoder.RecipeVersion, "test-only-family", new string('e', 64), true, true, true),
        () => null, media ?? RomMediaPolicyComposition.Create(), encodes: false);
    private sealed record PublishedCdArchive(Guid JobId, Guid PublicationId, RomRepresentationFacts Graph)
    {
        public Guid CreationId => Graph.Creations.Single().Id;
        public Guid VerificationId => Graph.Verifications.Single().Id;
    }
    private static RomChdDecoderBuild Upgraded(RomChdDecoderBuild build) => new(build.Rid, Sha1.Parse(new string('f', 40)), build.RecipeVersion + 1);
    private static readonly RomChdEncoderBuild Encoder = new("linux-x64", "mame0289", Sha256.Parse(new string('c', 64)), 1);
    private MediaReconciliationSnapshotReader Snapshots()
    {
        var descriptors = Substitute.For<IRomCdDescriptorIdentityReader>();
        var first = Identity(originals["disc.cue"]);
        var second = Identity(secondCue);
        descriptors.ReadAsync(3, first.Sha256, first.Size, Arg.Any<CancellationToken>()).Returns(first);
        descriptors.ReadAsync(5, second.Sha256, second.Size, Arg.Any<CancellationToken>()).Returns(second);
        return new(database.CreateOptions(), RomMediaPolicyComposition.Create().Kinds, descriptors);
    }
    private static MediaReconciliationDecision Decision(RomChdDecoderBuild decoder, bool canEncode, bool qualified)
    {
        var media = RomMediaPolicyComposition.Create();
        var capabilities = Substitute.For<IMediaHostCapabilities>();
        capabilities.CanDecode(Recipe.Kind).Returns(true);
        capabilities.CanEncode(Recipe.Kind).Returns(canEncode);
        capabilities.Decoder(RomRepresentationEncoding.Chd).Returns(decoder);
        capabilities.Qualifies(Recipe.Kind, decoder.Rid).Returns(qualified);
        capabilities.QualifiedRuntime(Recipe.Kind).Returns(decoder.Rid);
        capabilities.ManagedDecoderBuild.Returns("fixture-managed");
        var profile = Substitute.For<IMediaReconciliationMediaProfile>();
        profile.LimitsFingerprint.Returns(Sha256.Parse(new string('d', 64)));
        profile.OriginalInspectionDecoder.Returns(new RomDecoderIdentity.Managed("fixture-inspector"));
        profile.Encoder(Recipe.Kind).Returns(new MediaReconciliationEncoder(Encoder.BuildLabel, null));
        return new(media.Kinds, media.Reconciliation, capabilities, profile);
    }
    private MediaReconciliationExecution Execution(RomdDbContext db, MediaReconciliationDecision decision, bool stopped)
    {
        var custody = Substitute.For<IMediaRunCustody>();
        custody.EnsureJobStoppedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(stopped ? Result.Success : MediaReconciliationErrors.Custody);
        return new(db, database.CreateOptions(), new EfUnitOfWork(db), custody, TimeProvider.System, RomMediaPolicyComposition.Create().Kinds, decision);
    }
    private async Task<JobExecutionClaim<MediaReconciliationJob>> AcceptAndClaimAsync(RomdDbContext db, MediaReconciliationSubject subject, bool conversion)
    {
        Guid id;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var accepted = await new MediaReconciliationRequests(db, TimeProvider.System).StageSubjectAsync(
                new(Guid.NewGuid(), subject, conversion, MediaReconciliationCause.Admin, actor, null));
            accepted.IsError.ShouldBeFalse();
            id = accepted.Value.Job.Id;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        var repository = new MediaReconciliationJobRepository(db, TimeProvider.System);
        return (await new ClaimedJobRepository<MediaReconciliationJob, MediaReconciliationJobRepository>(repository, db, TimeProvider.System, new())
            .TryClaimExecutionAsync(id, "cd-publication-fixture")).ShouldNotBeNull();
    }
    private Task<PublishedCdArchive> Publish(bool commit = true) =>
        PublishLayout(1, Measured(), 9, new() { { "disc.cue", 3 } }, new() { { "track.bin", 1 }, { "audio.bin", 2 } }, commit);
    private async Task<PublishedCdArchive> PublishLayout(int layoutId, RomMeasuredRepresentation measured, int artifactFileId,
        Dictionary<string, int> stored, Dictionary<string, int> expectedOwners, bool commit = true)
    {
        await using var db = database.CreateContext();
        var claim = await AcceptAndClaimAsync(db, new MediaReconciliationSubject.OriginalLayout(layoutId), conversion: true);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var decision = Decision(Decoder, canEncode: true, qualified: true);
        var execution = Execution(db, decision, stopped: true);
        var demand = (await execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull();
        var facts = (await Snapshots().ReadAsync(demand.Subject)).Value;
        var directive = decision.Decide(claim.Job.Id, demand, facts).Value.ShouldBeOfType<MediaReconciliationDirective.Encode>();
        directive.Capture.Sources.Inputs.OfType<MediaReconciliationInput.Logical>().ToDictionary(input => input.Path!, input => input.Output.RomId!.Value)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal).ShouldBe(expectedOwners.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        var captured = await execution.CaptureAsync(directive.Capture);
        captured.IsError.ShouldBeFalse();
        db.Files.Add(new() { Id = artifactFileId, Sha256 = measured.Measurement.ArtifactSha256, Size = measured.Measurement.ArtifactSize, SizeOnDisk = measured.Measurement.ArtifactSize });
        await db.SaveChangesAsync();
        var manifest = (RomReconstructionManifest.CdTracks)measured.Manifest;
        var descriptors = manifest.Members.Select((member, ordinal) => (member, ordinal)).Where(pair => pair.member is RomCdStoredMember)
            .Select(pair => new RomRetainedDescriptor(pair.ordinal, stored[pair.member.Path], pair.member.Identity.Sha256, pair.member.Identity.Size)).ToArray();
        var artifact = Substitute.For<ITempFile>();
        artifact.FileSha256.Returns(measured.Measurement.ArtifactSha256);
        artifact.FileSize.Returns(measured.Measurement.ArtifactSize);
        var originals = manifest.Members.OfType<RomCdStoredMember>().ToDictionary(member => member.Path, member =>
        {
            var file = Substitute.For<ITempFile>();
            file.FileSha256.Returns(member.Identity.Sha256);
            file.FileSize.Returns(member.Identity.Size);
            return file;
        }, StringComparer.Ordinal);
        var encoded = new MediaReconciliationEncoded.Cd(artifact, new(artifact.FileSha256, Md5.Parse(new string('e', 32)), Sha1.Parse(new string('e', 40)), artifact.FileSize), measured, EncodingOf(measured, Encoder), originals, DateTimeOffset.UtcNow);
        await using (var transaction = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            (await execution.PrepareAsync(captured.Value, descriptors.Select(row => row.Sha256).Append(measured.Measurement.ArtifactSha256).ToArray())).IsError.ShouldBeFalse();
            (await execution.StageEncodedAsync(captured.Value, encoded, artifactFileId, descriptors)).IsError.ShouldBeFalse();
            if (!commit)
                throw new IOException("Injected publication failure after output/journal staging");
            await transaction.CommitAsync();
        }
        var outcome = (await execution.ReadAttemptOutcomeAsync(claim.Job.Id, captured.Value.Number)).ShouldNotBeNull();
        outcome.Disposition.ShouldBe(MediaReconciliationAttemptDisposition.Recorded);
        (await execution.FinishAsync(claim.Job.Id, demand.Version, MediaReconciliationOutcomeKind.Completed, "Canonical", processTreeStopped: true)).IsError.ShouldBeFalse();
        await new MediaRepresentationCleanup(db).CollectAsync(256);
        var retained = await ArchiveAsync(db, layoutId);
        var owner = await db.CdArchiveOwners.SingleAsync(row => row.LayoutId == layoutId);
        owner.VerificationId.ShouldBe(retained.Creations.Single().VerificationId);
        owner.RepresentationId.ShouldBe(retained.Id);
        owner.PublicationId.ShouldBe(claim.Job.Id);
        return new(claim.Job.Id, owner.PublicationId, retained);
    }
    private async Task<OriginalDiscLayoutDto> Decide(int layoutId, string action)
    {
        await using var db = database.CreateContext();
        var layout = await db.OriginalDiscLayouts.Include(x => x.Files).SingleAsync(x => x.Id == layoutId);
        OriginalDiscDependencyChoice[]? choices = null;
        if (action == "confirm")
        {
            // Confirmation requires a ROM for every catalog file; this fixture's DAT CUE template matches none.
            await db.CatalogReleaseFiles.Where(x => x.Name == "catalog.cue").ExecuteDeleteAsync();
            choices = layout.Files.Where(x => x.BindingRevision == Guid.Empty).Select(x => new OriginalDiscDependencyChoice(x.Path, IdCoder.Encode(x.RomId))).ToArray();
        }
        var result = await new DecideOriginalDiscLayoutCommandHandler(new OriginalDiscLayoutService(db, new FileMutationLock(db)), new EfUnitOfWork(db), MediaTestServices.CreateReconciliationRequests(db, TimeProvider.System), Microsoft.Extensions.Logging.Abstractions.NullLogger<DecideOriginalDiscLayoutCommandHandler>.Instance).HandleAsync(
            new(layoutId, new(action, action == "confirm" ? IdCoder.Encode(layoutId) : null, choices, "Reviewed"), RevisionPrecondition.For(layout.Revision.ToString()), actor));
        result.IsError.ShouldBeFalse(result.IsError ? string.Join(';', result.Errors) : "");
        return result.Value;
    }
    private async Task<ConsumerCdArchiveRepository> ConsumerArchives(RomdDbContext db, IRomChdDeliveryAdmission? admission = null)
    {
        db.Libraries.Add(new() { Id = 1, Name = "CD library", ConfigurationJson = "{}", ConfigurationState = "Valid", NeedsMaterialization = false });
        db.MaterializedLibraryReleases.Add(new()
        {
            LibraryId = 1,
            TitleId = 1,
            CatalogReleaseId = 1,
            DatGameId = 1,
            DatFileId = 1,
            PlatformId = 1,
            IsEligible = true,
            IsComplete = true,
            IsOwned = true,
            IsPlayable = true,
            IsExposed = true,
            ExposureReason = "ExposedDefault"
        });
        await db.SaveChangesAsync();
        await db.Users.Where(row => row.Id == actor).ExecuteUpdateAsync(set => set.SetProperty(row => row.LibraryId, 1));
        if (admission is null)
        { admission = Substitute.For<IRomChdDeliveryAdmission>(); admission.AssessCd(Arg.Any<RomReconstruction.CdMember>()).Returns(Decoder); }
        return new(db, Romd.PostgreSql.TestSupport.MediaTestServices.CreateContentCatalog(db), admission, RomMediaPolicyComposition.CreateDeliveryPolicy(), new DerivedDescriptorReader(db), database.CreateOptions());
    }
    private async Task SeedSecondReleaseAsync(Guid? sourceJob = null, bool bound = true)
    {
        await using var db = database.CreateContext();
        var track = Identity(secondTrack);
        var cue = Identity(secondCue);
        var audio = Identity(originals["audio.bin"]);
        db.CatalogReleases.Add(new() { Id = 2, PlatformId = 1, CatalogTitleId = 1, Fingerprint = "sha1:cd-second", Name = "Test (Europe)" });
        db.Files.AddRange(new() { Id = 4, Sha256 = track.Sha256, Size = track.Size, SizeOnDisk = track.Size }, new() { Id = 5, Sha256 = cue.Sha256, Size = cue.Size, SizeOnDisk = cue.Size });
        db.StageOriginalRom(new() { Id = 4, FileId = 4, OriginalFilename = "b-track.bin", ContentSha256 = track.Sha256, ContentSize = track.Size, Sha1 = track.Sha1, Md5 = track.Md5, Crc32 = track.Crc32 });
        db.CatalogReleaseFiles.AddRange(new() { CatalogReleaseId = 2, FileFingerprint = track.Sha1.ToString(), Name = "catalog-b-track.bin", Size = track.Size, Sha1 = track.Sha1 },
            new() { CatalogReleaseId = 2, FileFingerprint = audio.Sha1.ToString(), Name = "catalog-audio.bin", Size = audio.Size, Sha1 = audio.Sha1 });
        await db.PublishOriginalRomsAsync();
        db.OriginalDiscLayouts.Add(new()
        {
            Id = 2,
            State = "pending",
            SourceJobId = sourceJob ?? Guid.NewGuid(),
            SourceRelativePath = "b.cue",
            EntryPoint = "b.cue",
            DescriptorFileId = 5,
            DependencyPathsJson = "[\"b-track.bin\",\"audio.bin\"]",
            Revision = Guid.NewGuid(),
            Files = bound ? [new() { Path = "b-track.bin", RomId = 4, FileId = 4 }, new() { Path = "audio.bin", RomId = 2, FileId = 2 }] : []
        });
        await db.SaveChangesAsync();
    }

    private const string CdProfile = Romd.Media.PlayStation.ChdCdVerification.CurrentProfile;
    private const string CdInspector = "fixture-cd-inspector/v1";
    private RomMeasuredRepresentation Measured() => Measure(72,
        [new(1, 0, 64, RomCdTrackKind.Mode2Raw), new(2, 64, 5, RomCdTrackKind.Audio)],
        originals.Select((pair, index) => index < 2 ? (RomCdFileMember)new RomCdBinMember(pair.Key, Identity(pair.Value), Identity(pair.Value), [index + 1])
            : new RomCdStoredMember(pair.Key, Identity(pair.Value), RomCdStoredRole.Cue)).ToArray(),
        Identity(Enumerable.Range(0, 1024).Select(index => (byte)index).ToArray()), Sha1.Parse(new string('b', 40)), Decoder);
    private RomMeasuredRepresentation SecondMeasured()
    {
        var track = Identity(secondTrack);
        var audio = Identity(originals["audio.bin"]);
        return Measure(40, [new(1, 0, 32, RomCdTrackKind.Mode2Raw), new(2, 32, 5, RomCdTrackKind.Audio)],
            [new RomCdBinMember("b-track.bin",track,track,[1]),new RomCdBinMember("audio.bin",audio,audio,[2]),
                new RomCdStoredMember("b.cue",Identity(secondCue),RomCdStoredRole.Cue)],
            Identity(Enumerable.Range(0, 1024).Select(index => (byte)(255 - index)).ToArray()), Sha1.Parse(new string('d', 40)), Decoder);
    }
    private static RomMeasuredRepresentation Measure(long frames, IReadOnlyList<RomCdTrack> tracks, IReadOnlyList<RomCdFileMember> members,
        RomCdFileIdentity artifact, Sha1 raw, RomChdDecoderBuild decoder)
    {
        var manifest = new RomReconstructionManifest.CdTracks(frames, tracks, members);
        var container = new RomContainerFacts.Chd(5, frames * 2448, Recipe.HunkBytes, 2448,
            checked((uint)((frames * 2448 + Recipe.HunkBytes - 1) / Recipe.HunkBytes)), Recipe.Codecs, false, raw, raw,
            tracks.Select(track => new RomChdMetadataEntry(0x43485432, 1, Encoding.ASCII.GetBytes(
                $"TRACK:{track.Number} TYPE:{(track.Kind == RomCdTrackKind.Audio ? "AUDIO" : "MODE2_RAW")} SUBTYPE:NONE FRAMES:{track.Frames} PREGAP:0 PGTYPE:{(track.Kind == RomCdTrackKind.Audio ? "AUDIO" : "MODE2_RAW")} PGSUB:NONE POSTGAP:0"))).ToArray());
        var proof = new RomRepresentationVerification(Guid.NewGuid(), artifact.Sha256, artifact.Size, new RomDecoderIdentity.Native(decoder), CdProfile,
            "fixture-native-verifier/v1", DateTimeOffset.UtcNow, MeasuredOutputs(manifest), container, raw, raw);
        return new(container, manifest, proof);
    }
    private static RomMeasuredOutput[] MeasuredOutputs(RomReconstructionManifest.CdTracks manifest) =>
        manifest.Members.Select((member, ordinal) => (member, ordinal)).Where(pair => pair.member is RomCdBinMember)
            .Select(pair => new RomMeasuredOutput(new RomOutputSelector.Member(pair.ordinal), Logical(pair.member.Identity))).ToArray();
    private static RomLogicalIdentity Logical(RomCdFileIdentity member) => new(member.Size, member.Sha256, member.Sha1, member.Md5, member.Crc32);
    private static RomCdEncodingObservation EncodingOf(RomMeasuredRepresentation measured, RomChdEncoderBuild? encoder = null)
    {
        var manifest = (RomReconstructionManifest.CdTracks)measured.Manifest;
        return new(measured, RecipeId.Parse(Recipe.Id), encoder ?? new("linux-x64", "mame0289", Sha256.Parse(new string('c', 64)), 1),
            Sha256.Parse(new string('e', 64)), 100, CdInspector,
            new(RomPs1CdBoot.CurrentProfile, 0, checked((uint)manifest.Tracks.Sum(track => track.Frames)), 24, 2048, "SLUS_000.01;1", 26, 4096, 0x80010000, 0x80010000, 2048));
    }
    private static async Task<RomRepresentationFacts> ArchiveAsync(RomdDbContext db, int layout = 1)
    {
        int id = await db.CdArchiveOwners.Where(owner => owner.LayoutId == layout).Select(owner => owner.RepresentationId).SingleAsync();
        return (await new MediaFactReader(db).ReadAsync([id]))[id];
    }
    private static RomCdFileIdentity Identity(byte[] bytes) => new(bytes.Length, Sha256.FromSpan(SHA256.HashData(bytes)), Sha1.FromSpan(SHA1.HashData(bytes)), Md5.FromSpan(MD5.HashData(bytes)), Crc32.FromUInt32(System.IO.Hashing.Crc32.HashToUInt32(bytes)));
    public Task DisposeAsync() { database.Dispose(); Directory.Delete(directory, true); return Task.CompletedTask; }
}
