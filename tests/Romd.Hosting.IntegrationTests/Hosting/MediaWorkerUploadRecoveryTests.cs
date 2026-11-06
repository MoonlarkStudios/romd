using Romd.PostgreSql.TestSupport;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Application.Common.Content;
using Romd.Persistence.MediaFacts;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media.TestSupport;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Search;
using Shouldly;
using Xunit;
using static Romd.Hosting.IntegrationTests.Hosting.HangfirePostgreSqlTestSupport;
using static Romd.Hosting.IntegrationTests.Hosting.RomdHostProcesses;

namespace Romd.Hosting.IntegrationTests.Hosting;

[Collection(nameof(HangfirePostgreSqlColdStartCollection))]
public sealed class MediaWorkerUploadRecoveryTests
{
    [Fact]
    public async Task NormalWorker_MalformedRvz_FailsOnlyFileAndProcessesMatchedSiblingAndNextJob()
    {
        await using var postgres = await RomdPostgreSqlContainer.StartWithRomdRolesAsync();
        string directory = CreateDataDirectory("romd-media-worker-recovery");
        string marker = Path.Combine(directory, "worker.ready");
        string workerPath = Path.Combine(RepositoryRoot, "src", "Romd.Worker.Host", "bin", BuildConfiguration,
            "net10.0", "media-worker", "Romd.Media.Worker.dll");
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var options = DatabaseOptions(postgres.WorkerConnectionString);
        using Process worker = StartWorker(directory, postgres.WorkerConnectionString, postgres.ProvisionerConnectionString,
        [
            ("Romd__ReadinessMarkerPath", marker),
            ("Romd__Media__WorkerPath", workerPath),
            ("Romd__Media__MaxConcurrent", "1"),
            ("Enrichment__Providers__igdb__Enabled", "false"),
            ("Providers__Igdb__ClientId", ""),
            ("Providers__Igdb__ClientSecret", ""),
            ("DatSubscriptions__Enabled", "false")
        ]);
        int workerId = worker.Id;
        try
        {
            await WaitForReadyAsync(worker, marker, budget.Token);
            int platformId;
            await using (var db = new RomdDbContext(options))
                platformId = await db.Platforms.Where(platform => platform.CanonicalKey == "gc")
                    .Select(platform => platform.Id).SingleAsync(budget.Token);

            var disc = new SyntheticRvzBuilder(0x100000, 0x20000).Build();
            string sha1 = Convert.ToHexStringLower(SHA1.HashData(disc.ExpectedDisc));
            string md5 = Convert.ToHexStringLower(MD5.HashData(disc.ExpectedDisc));
            string crc = System.IO.Hashing.Crc32.HashToUInt32(disc.ExpectedDisc).ToString("x8");
            string dat = $"""
                <?xml version="1.0" encoding="utf-8"?>
                <datafile>
                  <header><name>Media containment</name><description>Media containment</description><version>1</version><author>ROMD test</author></header>
                  <game name="Containment disc"><description>Containment disc</description>
                    <rom name="sibling.iso" size="{disc.ExpectedDisc.Length}" crc="{crc}" md5="{md5}" sha1="{sha1}" />
                  </game>
                </datafile>
                """;
            var first = UploadJob.Create("containment/", createdAt: DateTimeOffset.UtcNow, platformId);
            first.SetArchiveOnly(true);
            first.SetMaxParallelRoms(1);
            string workspace = CreateWorkspace(directory, first.Id);
            await File.WriteAllTextAsync(Path.Combine(workspace, "containment.dat"), dat, budget.Token);
            await File.WriteAllBytesAsync(Path.Combine(workspace, "poison.rvz"), "RVZ\u0001"u8.ToArray(), budget.Token);
            await File.WriteAllBytesAsync(Path.Combine(workspace, "sibling.rvz"), disc.File, budget.Token);
            await AcceptAsync(options, first, budget.Token);

            var firstResult = await WaitForUploadAsync(options, first.Id, worker, budget.Token);
            firstResult.Phase.ShouldBe(UploadPhase.CompletedWithErrors.ToString());
            firstResult.DatsSucceeded.ShouldBe(1);
            firstResult.RomsProcessed.ShouldBe(2);
            firstResult.RomsIngested.ShouldBe(1);
            firstResult.RomsRejected.ShouldBe(0);
            firstResult.DeferralCount.ShouldBe(0);
            firstResult.NextAttemptAtUtc.ShouldBeNull();
            firstResult.GetErrors().ShouldHaveSingleItem().Item.ShouldBe("poison.rvz");
            var storage = CreateRuntimeStorage(postgres.WorkerConnectionString);
            await WaitForSucceededAsync(storage, firstResult.HangfireJobId!, worker, budget.Token);

            RomRepresentationVerification originalProof;
            Guid convergenceId;
            await using (var db = new RomdDbContext(options))
            {
                var items = await db.Set<JobItemEntity>().Where(item => item.JobId == first.Id).ToListAsync(budget.Token);
                items.Count.ShouldBe(3);
                items.Single(item => item.FileName == "poison.rvz").Outcome.ShouldBe((int)JobItemOutcome.Failed);
                items.Single(item => item.FileName == "poison.rvz").RomFileId.ShouldBeNull();
                items.Single(item => item.FileName == "sibling.rvz").Outcome.ShouldBe((int)JobItemOutcome.Ingested);
                items.Single(item => item.FileName == "containment.dat").Outcome.ShouldBe((int)JobItemOutcome.DatRouted);
                var rom = await db.RomFiles.SingleAsync(budget.Token);
                var content = (await MediaTestServices.CreateContentCatalog(db).FindByRomIdAsync(rom.Id, budget.Token)).ShouldNotBeNull();
                rom.Sha1.ShouldBe(Sha1.Parse(sha1));
                rom.ContentSha256.ShouldBe(Sha256.Parse(Convert.ToHexStringLower(SHA256.HashData(disc.ExpectedDisc))));
                rom.ContentSize.ShouldBe(disc.ExpectedDisc.LongLength);
                var representation = content.Artifacts.ShouldHaveSingleItem();
                representation.Encoding.ShouldBe(RomRepresentationEncoding.Rvz);
                representation.FileId.ShouldBe(content.FileId);
                originalProof = representation.Facts.Verifications.ShouldHaveSingleItem();
                (await db.Verifications.SingleAsync(budget.Token)).RepresentationId.ShouldBe(representation.RepresentationId);
                originalProof.CheckedProfile.ShouldBe(RvzVerifier.Profile);
                originalProof.ArtifactSha256.ShouldBe(Sha256.Parse(Convert.ToHexStringLower(SHA256.HashData(disc.File))));
                originalProof.Outputs.ShouldHaveSingleItem().Identity.Sha256.ShouldBe(rom.ContentSha256);
                convergenceId = (await db.Set<MediaReconciliationSubjectEntity>()
                    .SingleAsync(subject => subject.SubjectKind == "LogicalRom" && subject.SubjectId == rom.Id, budget.Token)).LatestJobId;
            }
            // General import demand independently inspects this synthetic RVZ. Its measurement mismatch must
            // stop and release the only media slot before the next upload, preserving the original complete proof.
            var convergence = await WaitForReconciliationAsync(options, convergenceId, worker, budget.Token);
            convergence.Definition.Outcome.ShouldNotBeNull().Kind.ShouldBe(MediaReconciliationOutcomeKind.Failed.ToString());
            convergence.Definition.Outcome.Code.ShouldBe(RomMediaVerificationErrors.Mismatch.Code);
            convergence.Phase.ShouldBe(MediaReconciliationPhase.Failed.ToString());
            convergence.DeferralCount.ShouldBe(0);
            convergence.NextAttemptAtUtc.ShouldBeNull();
            await WaitForSucceededAsync(storage, convergence.HangfireJobId!, worker, budget.Token);
            AssertNoRetainedMediaCustody(directory);

            var second = UploadJob.Create("after-poison/", createdAt: DateTimeOffset.UtcNow, platformId);
            second.SetArchiveOnly(true);
            await File.WriteAllBytesAsync(Path.Combine(CreateWorkspace(directory, second.Id), "sibling.rvz"), disc.File, budget.Token);
            await AcceptAsync(options, second, budget.Token);
            var secondResult = await WaitForUploadAsync(options, second.Id, worker, budget.Token);
            secondResult.Phase.ShouldBe(UploadPhase.Completed.ToString());
            secondResult.RomsProcessed.ShouldBe(1);
            secondResult.RomsDeduplicated.ShouldBe(1);
            secondResult.GetErrors().ShouldBeEmpty();
            secondResult.DeferralCount.ShouldBe(0);
            secondResult.NextAttemptAtUtc.ShouldBeNull();
            await WaitForSucceededAsync(storage, secondResult.HangfireJobId!, worker, budget.Token);
            await using (var db = new RomdDbContext(options))
            {
                var item = await db.Set<JobItemEntity>().SingleAsync(item => item.JobId == second.Id, budget.Token);
                item.Outcome.ShouldBe((int)JobItemOutcome.Deduplicated);
                var representationIds = await db.Verifications.Select(row => row.RepresentationId).Distinct().ToArrayAsync(budget.Token);
                var graphs = await new MediaFactReader(db).ReadAsync(representationIds, budget.Token);
                var proofs = graphs.Values.SelectMany(graph => graph.Verifications).ToList();
                // Re-uploading the same artifact with the same decoder retains the first immutable row.
                // Failed independent inspection did not create a second representation or decoder build.
                graphs.Values.ShouldHaveSingleItem();
                proofs.Count.ShouldBe(1);
                proofs.ShouldAllBe(proof => proof.CheckedProfile == RvzVerifier.Profile);
                var retained = proofs.Single(proof => proof.Id == originalProof.Id);
                retained.ObservedAt.ShouldBe(originalProof.ObservedAt);
                retained.Decoder.Key.ShouldBe(originalProof.Decoder.Key);
                retained.VerifierBuild.ShouldBe(originalProof.VerifierBuild);
                retained.ArtifactSha256.ShouldBe(originalProof.ArtifactSha256);
                retained.Outputs.Single().Identity.ShouldBe(originalProof.Outputs.Single().Identity);
            }
            AssertNoRetainedMediaCustody(directory);
            worker.Id.ShouldBe(workerId);
            worker.HasExited.ShouldBeFalse(RecentOutput(worker));
        }
        finally
        {
            if (!worker.HasExited)
                worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync(CancellationToken.None);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NormalWorker_MalformedChd_FailsOnlyFileAndProcessesMatchedSiblingAndNextJob()
    {
        await using var postgres = await RomdPostgreSqlContainer.StartWithRomdRolesAsync();
        string directory = CreateDataDirectory("romd-media-worker-chd-recovery");
        string marker = Path.Combine(directory, "worker.ready");
        string workerPath = Path.Combine(RepositoryRoot, "src", "Romd.Worker.Host", "bin", BuildConfiguration,
            "net10.0", "media-worker", "Romd.Media.Worker.dll");
        // A chdman-made singleton DVD CHD; its stored raw and overall SHA-1 let the child prove decoded integrity.
        byte[] sibling = await File.ReadAllBytesAsync(NativeTestBuilds.LibchdrFixture("dvd-zlib.chd"));
        byte[] logical = await File.ReadAllBytesAsync(NativeTestBuilds.LibchdrFixture("dvd-zlib.logical"));
        // Poison: an intact v5 header, metadata and map with one flipped byte inside the compressed hunk data.
        byte[] poison = sibling.ToArray();
        int mapOffset = checked((int)BinaryPrimitives.ReadUInt64BigEndian(poison.AsSpan(40, 8)));
        int firstHunk = checked((int)(BinaryPrimitives.ReadUInt64BigEndian(poison.AsSpan(mapOffset + 2, 8)) & 0xffffffffffff));
        firstHunk.ShouldBeGreaterThan(124);
        firstHunk.ShouldBeLessThan(mapOffset);
        poison[(firstHunk + mapOffset) / 2] ^= 0xff;
        // MediaInspectionErrors.Failed(MediaWorkerFailure.Malformed) in src/Romd.Infrastructure/Storage/RvzVerifier.cs;
        // only the child's decode rejection yields it, not an identity conflict or ordering path.
        const string malformed = "The media is malformed or its complete output could not be verified.";
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var options = DatabaseOptions(postgres.WorkerConnectionString);
        using Process worker = StartWorker(directory, postgres.WorkerConnectionString, postgres.ProvisionerConnectionString,
        [
            ("Romd__ReadinessMarkerPath", marker),
            ("Romd__Media__WorkerPath", workerPath),
            ("Romd__Media__MaxConcurrent", "1"),
            ("Romd__Native__LibchdrDirectory", NativeTestBuilds.LibchdrDirectory),
            ("Romd__Native__ChdmanDirectory", NativeTestBuilds.ChdmanDirectory),
            ("Enrichment__Providers__igdb__Enabled", "false"),
            ("Providers__Igdb__ClientId", ""),
            ("Providers__Igdb__ClientSecret", ""),
            ("DatSubscriptions__Enabled", "false")
        ]);
        int workerId = worker.Id;
        try
        {
            await WaitForReadyAsync(worker, marker, budget.Token);
            int platformId;
            await using (var db = new RomdDbContext(options))
                platformId = await db.Platforms.Where(platform => platform.CanonicalKey == "ps2")
                    .Select(platform => platform.Id).SingleAsync(budget.Token);

            string sha1 = Convert.ToHexStringLower(SHA1.HashData(logical));
            string md5 = Convert.ToHexStringLower(MD5.HashData(logical));
            string crc = System.IO.Hashing.Crc32.HashToUInt32(logical).ToString("x8");
            string dat = $"""
                <?xml version="1.0" encoding="utf-8"?>
                <datafile>
                  <header><name>CHD containment</name><description>CHD containment</description><version>1</version><author>ROMD test</author></header>
                  <game name="CHD containment disc"><description>CHD containment disc</description>
                    <rom name="sibling.iso" size="{logical.Length}" crc="{crc}" md5="{md5}" sha1="{sha1}" />
                  </game>
                </datafile>
                """;
            var first = UploadJob.Create("chd-containment/", createdAt: DateTimeOffset.UtcNow, platformId);
            first.SetArchiveOnly(true);
            first.SetMaxParallelRoms(1);
            string workspace = CreateWorkspace(directory, first.Id);
            await File.WriteAllTextAsync(Path.Combine(workspace, "containment.dat"), dat, budget.Token);
            await File.WriteAllBytesAsync(Path.Combine(workspace, "poison.chd"), poison, budget.Token);
            await File.WriteAllBytesAsync(Path.Combine(workspace, "sibling.chd"), sibling, budget.Token);
            await AcceptAsync(options, first, budget.Token);

            var firstResult = await WaitForUploadAsync(options, first.Id, worker, budget.Token);
            firstResult.Phase.ShouldBe(UploadPhase.CompletedWithErrors.ToString());
            firstResult.DatsSucceeded.ShouldBe(1);
            firstResult.RomsProcessed.ShouldBe(2);
            firstResult.RomsIngested.ShouldBe(1);
            firstResult.RomsRejected.ShouldBe(0);
            firstResult.DeferralCount.ShouldBe(0);
            firstResult.NextAttemptAtUtc.ShouldBeNull();
            var poisonError = firstResult.GetErrors().ShouldHaveSingleItem();
            poisonError.Item.ShouldBe("poison.chd");
            poisonError.Message.ShouldBe(malformed);
            var storage = CreateRuntimeStorage(postgres.WorkerConnectionString);
            await WaitForSucceededAsync(storage, firstResult.HangfireJobId!, worker, budget.Token);

            RomRepresentationVerification originalProof;
            Guid convergenceId;
            await using (var db = new RomdDbContext(options))
            {
                var items = await db.Set<JobItemEntity>().Where(item => item.JobId == first.Id).ToListAsync(budget.Token);
                items.Count.ShouldBe(3);
                var poisonItem = items.Single(item => item.FileName == "poison.chd");
                poisonItem.Outcome.ShouldBe((int)JobItemOutcome.Failed);
                poisonItem.RomFileId.ShouldBeNull();
                poisonItem.Error.ShouldBe(malformed);
                items.Single(item => item.FileName == "sibling.chd").Outcome.ShouldBe((int)JobItemOutcome.Ingested);
                items.Single(item => item.FileName == "containment.dat").Outcome.ShouldBe((int)JobItemOutcome.DatRouted);
                var rom = await db.RomFiles.SingleAsync(budget.Token);
                var content = (await MediaTestServices.CreateContentCatalog(db).FindByRomIdAsync(rom.Id, budget.Token)).ShouldNotBeNull();
                rom.Sha1.ShouldBe(Sha1.Parse(sha1));
                rom.ContentSha256.ShouldBe(Sha256.Parse(Convert.ToHexStringLower(SHA256.HashData(logical))));
                rom.ContentSize.ShouldBe(logical.LongLength);
                var representation = content.Artifacts.ShouldHaveSingleItem();
                representation.Encoding.ShouldBe(RomRepresentationEncoding.Chd);
                representation.FileId.ShouldBe(content.FileId);
                originalProof = representation.Facts.Verifications.ShouldHaveSingleItem();
                (await db.Verifications.SingleAsync(budget.Token)).RepresentationId.ShouldBe(representation.RepresentationId);
                originalProof.CheckedProfile.ShouldBe(ChdVerifier.Profile);
                originalProof.ArtifactSha256.ShouldBe(Sha256.Parse(Convert.ToHexStringLower(SHA256.HashData(sibling))));
                originalProof.Outputs.ShouldHaveSingleItem().Identity.Sha256.ShouldBe(rom.ContentSha256);
                originalProof.Decoder.ShouldBeOfType<RomDecoderIdentity.Native>();
                originalProof.ComputedRawSha1.ShouldBe(content.ContentSha1);
                originalProof.ComputedOverallSha1.ShouldBe(((RomContainerFacts.Chd)representation.Container).StoredOverallSha1);
                convergenceId = (await db.Set<MediaReconciliationSubjectEntity>()
                    .SingleAsync(subject => subject.SubjectKind == "LogicalRom" && subject.SubjectId == rom.Id, budget.Token)).LatestJobId;
            }
            // A complete CHD measurement without PS2 boot/reference authority settles current general demand
            // as ineligible. It must keep the verified upload and grant no encoding authority.
            var convergence = await WaitForReconciliationAsync(options, convergenceId, worker, budget.Token);
            convergence.Definition.Outcome.ShouldNotBeNull().Kind.ShouldBe(MediaReconciliationOutcomeKind.Completed.ToString());
            convergence.Definition.Outcome.Code.ShouldBe(MediaReconciliationNoWork.Ineligible.ToString());
            convergence.Phase.ShouldBe(MediaReconciliationPhase.Completed.ToString());
            convergence.DeferralCount.ShouldBe(0);
            convergence.NextAttemptAtUtc.ShouldBeNull();
            await WaitForSucceededAsync(storage, convergence.HangfireJobId!, worker, budget.Token);
            AssertNoRetainedMediaCustody(directory);

            var second = UploadJob.Create("after-chd-poison/", createdAt: DateTimeOffset.UtcNow, platformId);
            second.SetArchiveOnly(true);
            await File.WriteAllBytesAsync(Path.Combine(CreateWorkspace(directory, second.Id), "sibling.chd"), sibling, budget.Token);
            await AcceptAsync(options, second, budget.Token);
            var secondResult = await WaitForUploadAsync(options, second.Id, worker, budget.Token);
            secondResult.Phase.ShouldBe(UploadPhase.Completed.ToString());
            secondResult.RomsProcessed.ShouldBe(1);
            secondResult.RomsDeduplicated.ShouldBe(1);
            secondResult.GetErrors().ShouldBeEmpty();
            secondResult.DeferralCount.ShouldBe(0);
            secondResult.NextAttemptAtUtc.ShouldBeNull();
            await WaitForSucceededAsync(storage, secondResult.HangfireJobId!, worker, budget.Token);
            await using (var db = new RomdDbContext(options))
            {
                var item = await db.Set<JobItemEntity>().SingleAsync(item => item.JobId == second.Id, budget.Token);
                item.Outcome.ShouldBe((int)JobItemOutcome.Deduplicated);
                var rom = await db.RomFiles.SingleAsync(budget.Token);
                (await MediaTestServices.CreateContentCatalog(db).FindByRomIdAsync(rom.Id, budget.Token))!
                    .Artifacts.ShouldHaveSingleItem().Encoding.ShouldBe(RomRepresentationEncoding.Chd);
                // Re-observing the same decoder tuple retains its immutable first complete measurement.
                var representationIds = await db.Verifications.Select(row => row.RepresentationId).Distinct().ToArrayAsync(budget.Token);
                var graphs = await new MediaFactReader(db).ReadAsync(representationIds, budget.Token);
                var proofs = graphs.Values.SelectMany(graph => graph.Verifications).ToList();
                proofs.Count.ShouldBe(1);
                proofs.ShouldAllBe(proof => proof.CheckedProfile == ChdVerifier.Profile && proof.ArtifactSha256 == originalProof.ArtifactSha256);
                var retained = proofs.Single(proof => proof.Id == originalProof.Id);
                retained.ObservedAt.ShouldBe(originalProof.ObservedAt);
                retained.Decoder.Key.ShouldBe(originalProof.Decoder.Key);
                retained.VerifierBuild.ShouldBe(originalProof.VerifierBuild);
                retained.ArtifactSha256.ShouldBe(originalProof.ArtifactSha256);
                retained.Outputs.Single().Identity.ShouldBe(originalProof.Outputs.Single().Identity);
                proofs.ShouldAllBe(proof => proof.Decoder is RomDecoderIdentity.Native && proof.ComputedRawSha1.HasValue &&
                    proof.ComputedOverallSha1.HasValue);
            }
            AssertNoRetainedMediaCustody(directory);
            worker.Id.ShouldBe(workerId);
            worker.HasExited.ShouldBeFalse(RecentOutput(worker));
        }
        finally
        {
            if (!worker.HasExited)
                worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync(CancellationToken.None);
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Stable slot leases remain by design; no run record, identity sidecar or quarantine hold may.</summary>
    private static void AssertNoRetainedMediaCustody(string directory)
    {
        string runs = Path.Combine(directory, "temp", "media-runs-v1");
        if (Directory.Exists(runs))
            Directory.EnumerateFiles(runs, "run-*").ShouldBeEmpty();
        string staging = Path.Combine(directory, "temp", "staging-v1");
        if (Directory.Exists(staging))
            Directory.EnumerateFiles(staging, "*.quarantine").ShouldBeEmpty();
    }

    private static DbContextOptions<RomdDbContext> DatabaseOptions(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<RomdDbContext>().AddInterceptors(new SearchDocumentInterceptor());
        PostgreSqlConfiguration.Configure(builder, connectionString);
        return builder.Options;
    }

    private static string CreateWorkspace(string directory, Guid jobId)
    {
        string workspace = Path.Combine(directory, "temp", "jobs", jobId.ToString("N"));
        Directory.CreateDirectory(workspace);
        return workspace;
    }

    private static async Task AcceptAsync(DbContextOptions<RomdDbContext> options, UploadJob job, CancellationToken ct)
    {
        await using var db = new RomdDbContext(options);
        db.Set<UploadJobEntity>().Add(UploadJobEntity.FromDomain(job));
        await db.SaveChangesAsync(ct);
    }

    private static async Task WaitForReadyAsync(Process worker, string marker, CancellationToken ct)
    {
        while (!File.Exists(marker))
        {
            worker.HasExited.ShouldBeFalse(RecentOutput(worker));
            await Task.Delay(100, ct);
        }
    }

    private static async Task<UploadJobEntity> WaitForUploadAsync(DbContextOptions<RomdDbContext> options,
        Guid jobId, Process worker, CancellationToken ct)
    {
        while (true)
        {
            worker.HasExited.ShouldBeFalse(RecentOutput(worker));
            await using var db = new RomdDbContext(options);
            var job = await db.Jobs.OfType<UploadJobEntity>().SingleAsync(job => job.Id == jobId, ct);
            if (job.Phase is "Completed" or "CompletedWithErrors" or "Failed")
                return job;
            await Task.Delay(100, ct);
        }
    }

    private static async Task<MediaReconciliationJobEntity> WaitForReconciliationAsync(DbContextOptions<RomdDbContext> options,
        Guid jobId, Process worker, CancellationToken ct)
    {
        while (true)
        {
            worker.HasExited.ShouldBeFalse(RecentOutput(worker));
            await using var db = new RomdDbContext(options);
            var job = await db.Jobs.OfType<MediaReconciliationJobEntity>().Include(row => row.Definition).ThenInclude(row => row.Outcome)
                .SingleAsync(job => job.Id == jobId, ct);
            if (job.CompletedAt is not null)
                return job;
            await Task.Delay(100, ct);
        }
    }

    private static async Task WaitForSucceededAsync(Hangfire.JobStorage storage, string deliveryId,
        Process worker, CancellationToken ct)
    {
        while (true)
        {
            worker.HasExited.ShouldBeFalse(RecentOutput(worker));
            var state = storage.GetMonitoringApi().JobDetails(deliveryId)?.History.FirstOrDefault();
            state?.StateName.ShouldNotBe("Failed", state?.Reason);
            if (state?.StateName == "Succeeded")
                return;
            await Task.Delay(100, ct);
        }
    }
}
