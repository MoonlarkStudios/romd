using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Persistence;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Repositories;
using Romd.Persistence.Repositories.Reconciliation;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

/// <summary>Direct PostgreSQL guards precede the reconciliation repositories. No decoder qualification is implied.</summary>
public sealed partial class MediaReconciliationSchemaTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    public void Dispose() => _database.Dispose();
    private async Task<(RomdDbContext Db, MediaFactGraphFixture Graph)> SetupAsync()
    {
        var db = _database.CreateContext();
        try
        {
            return (db, await MediaFactGraphFixture.CreateAsync(db));
        }
        catch { await db.DisposeAsync(); throw; }
    }
    private static readonly RomChdDecoderBuild TargetDecoder = new("linux-arm64", MediaFactGraphFixture.Short('f'), 3);
    private sealed record Captured(Guid JobId, Guid Fence, RomRepresentationFacts Graph);
    private static async Task<Captured> CaptureAsync(RomdDbContext db, RomRepresentationFacts graph, MediaReconciliationSubject subject, RomChdDecoderBuild decoder, int sourceCount)
    {
        Guid job = Guid.NewGuid(), fence = Guid.NewGuid();
        long lease = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds();
        string kind = subject.Kind.ToString();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO romd."Jobs" ("Id","CorrelationId","SourceFilename","Phase","ErrorsJson","LastAttemptErrorTruncated","IsArchived",
                "UpdatedAt","DeferralCount","JobType","CreatedAt","ExecutionFenceToken","ExecutionLeaseExpiresAtUtc")
            VALUES ({job},{job},'retained archive','Planning','[]',false,false,0,0,'media-reconciliation',0,{fence},{lease});
            INSERT INTO romd."MediaReconciliationJobs" ("JobId","RequestMode","SubjectKind","SubjectId","CancelRequested")
                VALUES ({job},'Subject',{kind},{subject.Id},false);
            INSERT INTO romd."MediaReconciliationSubjects" ("SubjectKind","SubjectId","LatestJobId","RequestedVersion","AcknowledgedVersion","ConversionRequested")
                VALUES ({kind},{subject.Id},{job},1,0,false);
            INSERT INTO romd."MediaReconciliationRequests" ("Id","JobId","ActorId","Cause","RequestedVersion","ConversionRequested","CreatedAtUtcTicks")
                VALUES ({Guid.NewGuid()},{job},NULL,'Readiness',1,false,0);
            INSERT INTO romd."MediaReconciliationAttempts" ("JobId","Number","ExecutionFenceToken","RequestedVersion","Operation",
                "SourceFingerprint","CapabilityFingerprint","LimitsFingerprint","Fingerprint","SourceCount",
                "DecoderBuild","DecoderRid","DecoderUpstreamCommit","DecoderRecipeVersion","LogicalInspectionIds","StartedAtUtcTicks")
            VALUES ({job},1,{fence},1,'Verify',{MediaFactGraphFixture.Hash('a').ToArray()},{MediaFactGraphFixture.Hash('b').ToArray()},
                {MediaFactGraphFixture.Hash('c').ToArray()},{MediaFactGraphFixture.Hash('d').ToArray()},{sourceCount},
                {decoder.BuildLabel},{decoder.Rid},{decoder.UpstreamCommit.ToArray()},{decoder.RecipeVersion},ARRAY[]::uuid[],0);
            INSERT INTO romd."MediaReconciliationInputs" ("JobId","AttemptNumber","Ordinal","Role","FileId","ArtifactSha256","ArtifactSize","RepresentationId","ParametersSha256")
                VALUES ({job},1,0,'Artifact',{graph.FileId},{graph.ArtifactSha256.ToArray()},{graph.ArtifactSize},{graph.Id},{graph.Container.Fingerprint!.Value.ToArray()});
            INSERT INTO romd."MediaReconciliationPins" ("JobId","AttemptNumber","Ordinal","FileId","RepresentationId")
                VALUES ({job},1,0,{graph.FileId},{graph.Id});
            """);
        await transaction.CommitAsync();
        return new(job, fence, graph);
    }
    private static async Task RejectAsync(RomdDbContext db, FormattableString mutation, bool atCommit)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var error = await Should.ThrowAsync<PostgresException>(async () =>
        {
            if (atCommit)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(mutation);
                await transaction.CommitAsync();
            }
            else
                await db.Database.ExecuteSqlInterpolatedAsync(mutation);
        });
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("request")]
    [InlineData("attempt")]
    [InlineData("input")]
    public async Task CapturedWork_IdentityMutation_RefusesWithoutChangingDurableCustody(string mutation)
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var captured = await CaptureAsync(db, graph.Cd, new MediaReconciliationSubject.Representation(graph.Cd.Id), TargetDecoder, 1);
        FormattableString sql = mutation switch
        {
            "scope" => $"UPDATE romd.\"MediaReconciliationJobs\" SET \"SubjectId\" = 999 WHERE \"JobId\" = {captured.JobId}",
            "request" => $"UPDATE romd.\"MediaReconciliationRequests\" SET \"ActorId\" = {Guid.NewGuid()} WHERE \"JobId\" = {captured.JobId}",
            "attempt" => $"UPDATE romd.\"MediaReconciliationAttempts\" SET \"LimitsFingerprint\" = {MediaFactGraphFixture.Hash('e').ToArray()} WHERE \"JobId\" = {captured.JobId}",
            _ => $"UPDATE romd.\"MediaReconciliationInputs\" SET \"ArtifactSha256\" = {MediaFactGraphFixture.Hash('e').ToArray()} WHERE \"JobId\" = {captured.JobId}"
        };
        await RejectAsync(db, sql, atCommit: false);
        (await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM romd.\"MediaReconciliationPins\" WHERE \"JobId\" = {captured.JobId}").SingleAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Capture_PartialVector_RefusesCommitInsteadOfPublishingPartialCustody()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var error = await Should.ThrowAsync<PostgresException>(() => CaptureAsync(db, graph.Cd, new MediaReconciliationSubject.Representation(graph.Cd.Id), TargetDecoder, 2));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        (await db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM romd.\"MediaReconciliationJobs\"").SingleAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Pin_AnotherPhysicalFile_RefusesBeforeChangingTheDependency()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var captured = await CaptureAsync(db, graph.Cd, new MediaReconciliationSubject.Representation(graph.Cd.Id), TargetDecoder, 1);
        await RejectAsync(db, $"UPDATE romd.\"MediaReconciliationPins\" SET \"FileId\" = 3 WHERE \"JobId\" = {captured.JobId}", atCommit: false);
    }

    [Fact]
    public async Task Pin_ReleasedWithoutStoppedOutcome_RefusesAndRetainsEverySource()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var captured = await CaptureAsync(db, graph.Cd, new MediaReconciliationSubject.Representation(graph.Cd.Id), TargetDecoder, 1);
        await RejectAsync(db, $"DELETE FROM romd.\"MediaReconciliationPins\" WHERE \"JobId\" = {captured.JobId}", atCommit: false);
    }

    [Fact]
    public async Task Attempt_OlderDecoderProof_RefusesAClaimForTheCapturedBuild()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var captured = await CaptureAsync(db, graph.Cd, new MediaReconciliationSubject.Representation(graph.Cd.Id), TargetDecoder, 1);
        await RejectAsync(db, $"""
            UPDATE romd."MediaReconciliationAttempts" SET "Outcome"='Recorded',"OutcomeCode"='verified',"Deterministic"=false,
                "ResultRepresentationId"={graph.Cd.Id},"ResultVerificationId"={graph.Cd.Verifications.Single().Id},"CompletedAtUtcTicks"=10,"StoppedAtUtcTicks"=10
                WHERE "JobId"={captured.JobId}
            """, atCommit: false);
    }

    [Fact]
    public async Task Job_UnjournaledTerminalState_RefusesEvenWhenPhaseLooksComplete()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var captured = await CaptureAsync(db, graph.Cd, new MediaReconciliationSubject.Representation(graph.Cd.Id), TargetDecoder, 1);
        await RejectAsync(db, $"UPDATE romd.\"Jobs\" SET \"Phase\"='Completed',\"CompletedAt\"=1 WHERE \"Id\"={captured.JobId}", atCommit: true);
    }

    [Fact]
    public async Task Demand_NewConversionRequest_CannotRegressItsVersionOrDropTheActiveExplicitDemand()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var captured = await CaptureAsync(db, graph.Cd, new MediaReconciliationSubject.LogicalRom(graph.RomId), TargetDecoder, 1);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE romd.\"MediaReconciliationSubjects\" SET \"RequestedVersion\"=2,\"ConversionRequested\"=true WHERE \"LatestJobId\"={captured.JobId}");
        await RejectAsync(db, $"UPDATE romd.\"MediaReconciliationSubjects\" SET \"RequestedVersion\"=1 WHERE \"LatestJobId\"={captured.JobId}", atCommit: false);
        await RejectAsync(db, $"UPDATE romd.\"MediaReconciliationSubjects\" SET \"ConversionRequested\"=false WHERE \"LatestJobId\"={captured.JobId}", atCommit: false);
    }

    private static RomRepresentationVerification Measurement(RomRepresentationFacts graph, RomChdDecoderBuild decoder) =>
        new(Guid.NewGuid(), graph.ArtifactSha256, graph.ArtifactSize, new RomDecoderIdentity.Native(decoder),
            Romd.Media.Cd.ChdCdVerification.CurrentProfile, "fixture-verifier", DateTimeOffset.UnixEpoch.AddTicks(10), graph.Verifications.Single().Outputs, graph.Container,
            ((RomContainerFacts.Chd)graph.Container).StoredRawSha1, ((RomContainerFacts.Chd)graph.Container).StoredOverallSha1);

    private static async Task StageRecordedAsync(RomdDbContext db, Captured captured, RomRepresentationVerification measured)
    {
        (await new MediaFactPublisher(db).AppendVerificationAsync(captured.Graph.Id, measured)).ShouldBeTrue();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE romd."MediaReconciliationAttempts" SET "Outcome"='Recorded',"OutcomeCode"='verified',"Deterministic"=false,
                "ResultRepresentationId"={captured.Graph.Id},"ResultVerificationId"={measured.Id},"CompletedAtUtcTicks"={DateTimeOffset.UnixEpoch.UtcTicks + 10},
                "StoppedAtUtcTicks"={DateTimeOffset.UnixEpoch.UtcTicks + 10} WHERE "JobId"={captured.JobId} AND "Number"=1;
            DELETE FROM romd."MediaReconciliationPins" WHERE "JobId"={captured.JobId};
            """);
    }

    private static Task FinishSubjectAsync(RomdDbContext db, Captured captured, long version) => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO romd."MediaReconciliationOutcomes" ("JobId","Kind","Code","AcknowledgedVersion","ExecutionFenceToken","CompletedAtUtcTicks")
            VALUES ({captured.JobId},'Completed','verified',{version},{captured.Fence},{DateTimeOffset.UnixEpoch.UtcTicks + 10});
        UPDATE romd."MediaReconciliationSubjects" SET "AcknowledgedVersion"={version} WHERE "LatestJobId"={captured.JobId};
        UPDATE romd."Jobs" SET "Phase"='Completed',"CompletedAt"=0 WHERE "Id"={captured.JobId};
        """);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Completion_MeasurementJournalAcknowledgementAndRelease_CommitOrRollbackTogether(bool commit)
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var captured = await CaptureAsync(db, graph.Cd, new MediaReconciliationSubject.Representation(graph.Cd.Id), TargetDecoder, 1);
        var measured = Measurement(graph.Cd, TargetDecoder);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await StageRecordedAsync(db, captured, measured);
            await FinishSubjectAsync(db, captured, version: 1);
            if (commit)
                await transaction.CommitAsync();
            else
                await transaction.RollbackAsync();
        }
        var retained = (await new MediaFactReader(db).ReadAsync([graph.Cd.Id]))[graph.Cd.Id];
        retained.Verifications.Count.ShouldBe(commit ? 2 : 1);
        var original = graph.Cd.Verifications.Single();
        var preserved = retained.Verifications.Single(proof => proof.Id == original.Id);
        (preserved.ArtifactSha256, preserved.ArtifactSize, preserved.Decoder, preserved.CheckedProfile, preserved.VerifierBuild, preserved.ObservedAt,
            preserved.ObservedParametersSha256, preserved.ComputedRawSha1, preserved.ComputedOverallSha1).ShouldBe(
            (original.ArtifactSha256, original.ArtifactSize, original.Decoder, original.CheckedProfile, original.VerifierBuild, original.ObservedAt,
             original.ObservedParametersSha256, original.ComputedRawSha1, original.ComputedOverallSha1));
        preserved.Outputs.ShouldBe(original.Outputs);
        retained.Verifications.Any(proof => proof.Id == measured.Id).ShouldBe(commit);
        (await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM romd.\"MediaReconciliationPins\" WHERE \"JobId\"={captured.JobId}").SingleAsync()).ShouldBe(commit ? 0 : 1);
        (await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM romd.\"MediaReconciliationOutcomes\" WHERE \"JobId\"={captured.JobId}").SingleAsync()).ShouldBe(commit ? 1 : 0);
        (await db.Database.SqlQuery<long>($"SELECT \"AcknowledgedVersion\" AS \"Value\" FROM romd.\"MediaReconciliationSubjects\" WHERE \"LatestJobId\"={captured.JobId}").SingleAsync()).ShouldBe(commit ? 1 : 0);
        (await db.Database.SqlQuery<string>($"SELECT \"Phase\" AS \"Value\" FROM romd.\"Jobs\" WHERE \"Id\"={captured.JobId}").SingleAsync()).ShouldBe(commit ? "Completed" : "Planning");
    }

    [Fact]
    public async Task CapturedHistory_AfterStoppedCompletionAndOwnerWithdrawal_DoesNotOwnTheGraph()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var captured = await CaptureAsync(db, graph.Cd, new MediaReconciliationSubject.Representation(graph.Cd.Id), TargetDecoder, 1);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM romd.\"Roms\" WHERE \"Id\"={graph.RomId}");
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            (await db.Database.SqlQuery<bool>($"SELECT romd.delete_unowned_media_representation({graph.Cd.Id}) AS \"Value\"").SingleAsync()).ShouldBeFalse();
            await transaction.CommitAsync();
        }
        (await new FileRepository(db).IsReferencedAsync(graph.Cd.FileId, CancellationToken.None)).ShouldBeTrue();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await StageRecordedAsync(db, captured, Measurement(graph.Cd, TargetDecoder));
            await FinishSubjectAsync(db, captured, version: 1);
            await transaction.CommitAsync();
        }
        // Immutable history and result IDs deliberately carry no ownership after the active pin is released.
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            (await db.Database.SqlQuery<bool>($"SELECT romd.delete_unowned_media_representation({graph.Cd.Id}) AS \"Value\"").SingleAsync()).ShouldBeTrue();
            await transaction.CommitAsync();
        }
        (await new FileRepository(db).IsReferencedAsync(graph.Cd.FileId, CancellationToken.None)).ShouldBeFalse();
        (await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM romd.\"Verifications\" WHERE \"RepresentationId\"={graph.Cd.Id}").SingleAsync()).ShouldBe(0);
        (await db.Database.SqlQuery<int>($"SELECT \"RepresentationId\" AS \"Value\" FROM romd.\"MediaReconciliationInputs\" WHERE \"JobId\"={captured.JobId}").SingleAsync()).ShouldBe(graph.Cd.Id);
        (await db.Database.SqlQuery<int>($"SELECT \"ResultRepresentationId\" AS \"Value\" FROM romd.\"MediaReconciliationAttempts\" WHERE \"JobId\"={captured.JobId}").SingleAsync()).ShouldBe(graph.Cd.Id);
        (await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM romd.\"MediaReconciliationOutcomes\" WHERE \"JobId\"={captured.JobId}").SingleAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Completion_RacedExplicitRequest_PreventsStaleAcknowledgementAndRollsBackPublication()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var captured = await CaptureAsync(db, graph.Cd, new MediaReconciliationSubject.LogicalRom(graph.RomId), TargetDecoder, 1);
        await using (var requester = _database.CreateContext())
            await requester.Database.ExecuteSqlInterpolatedAsync($"UPDATE romd.\"MediaReconciliationSubjects\" SET \"RequestedVersion\"=2,\"ConversionRequested\"=true WHERE \"LatestJobId\"={captured.JobId}");
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await StageRecordedAsync(db, captured, Measurement(graph.Cd, TargetDecoder));
            var error = await Should.ThrowAsync<PostgresException>(() => FinishSubjectAsync(db, captured, version: 1));
            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            await transaction.RollbackAsync();
        }
        (await db.Database.SqlQuery<long>($"SELECT \"RequestedVersion\" AS \"Value\" FROM romd.\"MediaReconciliationSubjects\" WHERE \"LatestJobId\"={captured.JobId}").SingleAsync()).ShouldBe(2);
        (await db.Database.SqlQuery<bool>($"SELECT \"ConversionRequested\" AS \"Value\" FROM romd.\"MediaReconciliationSubjects\" WHERE \"LatestJobId\"={captured.JobId}").SingleAsync()).ShouldBeTrue();
        (await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM romd.\"MediaReconciliationPins\" WHERE \"JobId\"={captured.JobId}").SingleAsync()).ShouldBe(1);
        (await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM romd.\"MediaReconciliationAttempts\" WHERE \"JobId\"={captured.JobId} AND \"Outcome\" IS NULL").SingleAsync()).ShouldBe(1);
        (await new MediaFactReader(db).ReadAsync([graph.Cd.Id]))[graph.Cd.Id].Verifications.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailureCache_OnlyDeterministicStoppedAttempts_AreRetained(bool deterministic)
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var captured = await CaptureAsync(db, graph.Cd, new MediaReconciliationSubject.Representation(graph.Cd.Id), TargetDecoder, 1);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE romd."MediaReconciliationAttempts" SET "Outcome"='Failed',"OutcomeCode"='decoder-failed',"Deterministic"={deterministic},
                    "CompletedAtUtcTicks"=10,"StoppedAtUtcTicks"=10 WHERE "JobId"={captured.JobId};
                DELETE FROM romd."MediaReconciliationPins" WHERE "JobId"={captured.JobId};
                """);
            FormattableString cache = $"INSERT INTO romd.\"MediaReconciliationFailures\" (\"Fingerprint\",\"JobId\",\"AttemptNumber\") VALUES ({MediaFactGraphFixture.Hash('d').ToArray()},{captured.JobId},1)";
            if (deterministic)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(cache);
                await transaction.CommitAsync();
            }
            else
            {
                var error = await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(cache));
                error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
                await transaction.RollbackAsync();
            }
        }
        (await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM romd.\"MediaReconciliationFailures\" WHERE \"JobId\"={captured.JobId}").SingleAsync()).ShouldBe(deterministic ? 1 : 0);
        (await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM romd.\"MediaReconciliationPins\" WHERE \"JobId\"={captured.JobId}").SingleAsync()).ShouldBe(deterministic ? 0 : 1);
        if (deterministic)
            await RejectAsync(db, $"DELETE FROM romd.\"MediaReconciliationFailures\" WHERE \"JobId\"={captured.JobId}", atCommit: false);
    }

    [Theory]
    [InlineData("replaced")]
    [InlineData("expired")]
    public async Task Attempt_LostExecutionFence_CannotReleaseCustody(string loss)
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var captured = await CaptureAsync(db, graph.Cd, new MediaReconciliationSubject.Representation(graph.Cd.Id), TargetDecoder, 1);
        if (loss == "replaced")
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE romd.\"Jobs\" SET \"ExecutionFenceToken\"={Guid.NewGuid()} WHERE \"Id\"={captured.JobId}");
        else
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE romd.\"Jobs\" SET \"ExecutionLeaseExpiresAtUtc\"=0 WHERE \"Id\"={captured.JobId}");
        await RejectAsync(db, $"""
            UPDATE romd."MediaReconciliationAttempts" SET "Outcome"='Cancelled',"OutcomeCode"='cancelled',"Deterministic"=false,
                "CompletedAtUtcTicks"=10,"StoppedAtUtcTicks"=10 WHERE "JobId"={captured.JobId}
            """, atCommit: false);
        (await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM romd.\"MediaReconciliationPins\" WHERE \"JobId\"={captured.JobId}").SingleAsync()).ShouldBe(1);
    }
}
