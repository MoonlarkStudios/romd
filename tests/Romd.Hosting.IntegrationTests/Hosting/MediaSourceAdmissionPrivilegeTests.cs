using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Npgsql;
using Romd.Admin.Application.Source.Rom;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Persistence.Repositories.Reconciliation;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class MediaSourceAdmissionPrivilegeTests
{
    [Fact]
    public async Task Accept_ProductionAdminRole_StagesLayoutDemandWithoutPrematurePinsOrMediaFactWriteAuthority()
    {
        await using var postgres = await RomdPostgreSqlContainer.StartWithRomdRolesAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{PostgreSqlConfiguration.ProvisioningConnectionName}"] = postgres.ProvisionerConnectionString
        }).Build();
        await new PostgreSqlSchemaProvisioner(configuration, NullLogger<PostgreSqlSchemaProvisioner>.Instance).StartAsync(default);
        Guid actor = Guid.NewGuid(), revision = Guid.NewGuid(), requestId = Guid.NewGuid();
        var bin = MediaFactFixtures.Identity(Enumerable.Range(0, 5 * 2352).Select(i => (byte)i).ToArray());
        var cue = MediaFactFixtures.Identity(Encoding.ASCII.GetBytes("FILE \"track.bin\" BINARY\n TRACK 01 MODE2/2352\n INDEX 01 00:00:00\n"));
        int romId, representationId, outputId, layoutId;
        await using (var setup = Context(postgres.WorkerConnectionString))
        {
            setup.Users.Add(new() { Id = actor, UserName = "admission", NormalizedUserName = "ADMISSION", SecurityStamp = Guid.NewGuid().ToString() });
            setup.Platforms.Add(new()
            {
                Id = 1,
                Name = "PlayStation",
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
                BuiltInVersion = 1,
                BaseName = "PlayStation",
                BaseCompactLabel = "PS",
                ShortName = "psx",
                CanonicalKey = "psx"
            });
            setup.Titles.Add(new() { Id = 1, PlatformId = 1, Name = "Game", NormalizedName = "game", EnrichmentStatus = "Completed" });
            setup.CatalogReleases.Add(new() { Id = 1, PlatformId = 1, CatalogTitleId = 1, Fingerprint = "sha1:admin-cd", Name = "Game" });
            setup.CatalogReleaseFiles.Add(new() { CatalogReleaseId = 1, FileFingerprint = bin.Sha1.ToString(), Name = "track.bin", Size = bin.Size, Sha1 = bin.Sha1 });
            setup.Files.AddRange(new FileEntityPersistence { Id = 1, Sha256 = bin.Sha256, Size = bin.Size, SizeOnDisk = bin.Size },
                new FileEntityPersistence { Id = 2, Sha256 = cue.Sha256, Size = cue.Size, SizeOnDisk = cue.Size });
            await setup.SaveChangesAsync();
            await using (var publication = await setup.Database.BeginTransactionAsync())
            {
                var container = RomContainerFacts.Identity.Instance;
                var proof = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), bin.Sha256, bin.Size, bin, container,
                    new RomDecoderIdentity.Managed("original-bytes/role-test"), "bytes/role-test", "verifier/role-test", DateTimeOffset.UnixEpoch, null, null);
                var original = await setup.CreatePreferredMeasuredWholeAsync("track.bin", 1, container, proof, actor, DateTimeOffset.UnixEpoch, default);
                romId = original.RomId;
                representationId = original.Graph.Id;
                outputId = original.Output.Id;
                await publication.CommitAsync();
            }
            var layout = new OriginalDiscLayoutEntity
            {
                SourceJobId = Guid.NewGuid(),
                SourceRelativePath = "disc.cue",
                EntryPoint = "disc.cue",
                DescriptorFileId = 2,
                DependencyPathsJson = "[\"track.bin\"]",
                Revision = revision,
                Files = [new() { Path = "track.bin", RomId = romId, FileId = 1 }]
            };
            setup.OriginalDiscLayouts.Add(layout);
            await setup.SaveChangesAsync();
            layoutId = layout.Id;
        }
        await using var admin = Context(postgres.AdminConnectionString);
        var requests = new MediaReconciliationRequests(admin, TimeProvider.System);
        Guid jobId;
        await using (var transaction = await admin.Database.BeginTransactionAsync())
        {
            var demand = new Romd.Admin.Application.Source.Rom.Reconciliation.MediaReconciliationDemand(requestId,
                new MediaReconciliationSubject.OriginalLayout(layoutId), true, Romd.Admin.Application.Source.Rom.Reconciliation.MediaReconciliationCause.Admin, actor, null);
            var accepted = await requests.StageSubjectAsync(demand);
            accepted.IsError.ShouldBeFalse();
            jobId = accepted.Value.Job.Id;
            accepted.Value.Created.ShouldBeTrue();
            await admin.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        (await admin.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
        var audit = await admin.Set<MediaReconciliationRequestEntity>().SingleAsync();
        audit.Id.ShouldBe(requestId);
        audit.JobId.ShouldBe(jobId);
        audit.ActorId.ShouldBe(actor);
        audit.ConversionRequested.ShouldBeTrue();
        await using (var transaction = await admin.Database.BeginTransactionAsync())
        {
            var replay = await requests.StageSubjectAsync(new(requestId, new MediaReconciliationSubject.OriginalLayout(layoutId), true,
                Romd.Admin.Application.Source.Rom.Reconciliation.MediaReconciliationCause.Admin, actor, null));
            replay.IsError.ShouldBeFalse();
            replay.Value.Job.Id.ShouldBe(jobId);
            replay.Value.Created.ShouldBeFalse();
            await admin.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        await using (var transaction = await admin.Database.BeginTransactionAsync())
        {
            var retained = await MediaTestServices.CreateRomRepository(admin).ListRepresentationsAsync(romId);
            retained.Single().RepresentationId.ShouldBe(representationId);
            retained.Single().OutputId.ShouldBe(outputId);
            await transaction.CommitAsync();
        }
        foreach (string invalid in new[]
        {
            "SELECT romd.lock_media_logical_rom(0)", "SELECT romd.lock_media_logical_rom(NULL)",
            "SELECT romd.lock_media_representation(0)", "SELECT romd.lock_media_representation(NULL)"
        })
            (await Should.ThrowAsync<PostgresException>(() => admin.Database.ExecuteSqlRawAsync(invalid))).SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        foreach (string guarded in new[] { "SELECT romd.lock_media_logical_rom(1)", "SELECT romd.lock_media_representation(1)" })
        {
            await using (var repeatableRead = await admin.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead))
                (await Should.ThrowAsync<PostgresException>(() => admin.Database.ExecuteSqlRawAsync(guarded))).SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            await using var consumer = Context(postgres.ConsumerConnectionString);
            (await Should.ThrowAsync<PostgresException>(() => consumer.Database.ExecuteSqlRawAsync(guarded))).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }
        foreach (string sql in new[]
        {
            """UPDATE romd."Roms" SET "Id" = "Id" WHERE false""",
            """UPDATE romd."Representations" SET "Id" = "Id" WHERE false""",
            """UPDATE romd."RepresentationOutputs" SET "Id" = "Id" WHERE false""",
            """UPDATE romd."Verifications" SET "Id" = "Id" WHERE false""",
            """INSERT INTO romd."MediaReconciliationPins" ("JobId","AttemptNumber","Ordinal","FileId") SELECT NULL,1,0,1 WHERE false""",
            """UPDATE romd."MediaReconciliationAttempts" SET "Number" = "Number" WHERE false"""
        })
        {
            var denied = await Should.ThrowAsync<PostgresException>(() => admin.Database.ExecuteSqlRawAsync(sql));
            denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }
    }

    private static RomdDbContext Context(string connectionString)
    {
        var options = new DbContextOptionsBuilder<RomdDbContext>();
        PostgreSqlConfiguration.Configure(options, connectionString);
        return new(options.Options);
    }
}
