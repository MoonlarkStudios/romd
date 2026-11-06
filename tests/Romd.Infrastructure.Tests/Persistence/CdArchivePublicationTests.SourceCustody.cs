using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class CdArchivePublicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentQualificationChanged_RefusesTheCapturedCdOperationBeforeAnyPinOrProof(bool verify)
    {
        var publication = verify ? await Publish() : null;
        await using var db = database.CreateContext();
        MediaReconciliationSubject subject = verify
            ? new MediaReconciliationSubject.Representation(publication!.Graph.Id)
            : new MediaReconciliationSubject.OriginalLayout(1);
        var decoder = verify ? Upgraded(Decoder) : Decoder;
        var claim = await AcceptAndClaimAsync(db, subject, conversion: !verify);
        var decision = Decision(decoder, canEncode: !verify, qualified: true);
        var execution = Execution(db, decision, stopped: true);
        var demand = (await execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull();
        var facts = (await Snapshots().ReadAsync(subject)).Value;
        var planned = decision.Decide(claim.Job.Id, demand, facts).Value;
        var capture = verify ? planned.ShouldBeOfType<MediaReconciliationDirective.Verify>().Capture
            : planned.ShouldBeOfType<MediaReconciliationDirective.Encode>().Capture;
        var current = Decision(decoder, canEncode: !verify, qualified: false);
        current.Decide(claim.Job.Id, demand, facts).Value.ShouldBeOfType<MediaReconciliationDirective.Unavailable>();
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        if (!verify)
            (await Execution(db, current, stopped: true).CaptureAsync(capture)).FirstError.ShouldBe(MediaReconciliationErrors.Changed);
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
        if (verify)
            (await ArchiveAsync(db)).Verifications.ShouldHaveSingleItem().Id.ShouldBe(publication!.VerificationId);
        else
            (await db.CdArchiveOwners.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Capture_DescriptorIdentityChangedAfterSnapshot_RefusesWithoutPartialInputsOrPins()
    {
        await using var db = database.CreateContext();
        var subject = new MediaReconciliationSubject.OriginalLayout(1);
        var claim = await AcceptAndClaimAsync(db, subject, conversion: true);
        var decision = Decision(Decoder, canEncode: true, qualified: true);
        var execution = Execution(db, decision, stopped: true);
        var demand = (await execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull();
        var facts = (await Snapshots().ReadAsync(subject)).Value;
        var capture = decision.Decide(claim.Job.Id, demand, facts).Value.ShouldBeOfType<MediaReconciliationDirective.Encode>().Capture;
        await using (var mutation = database.CreateContext())
            (await mutation.Files.Where(file => file.Id == 3).ExecuteUpdateAsync(set => set.SetProperty(file => file.Size, file => file.Size + 1))).ShouldBe(1);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        (await execution.CaptureAsync(capture)).FirstError.ShouldBe(MediaReconciliationErrors.Changed);
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
        (await db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM romd.\"MediaReconciliationInputs\"").SingleAsync()).ShouldBe(0);
        (await db.CdArchiveOwners.CountAsync()).ShouldBe(0);
        (await execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull().Version.ShouldBe(demand.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturedDescriptor_FileIdentityCannotChangeAndSourcesRemainUntilStoppedSettlement(bool changeHash)
    {
        await using var db = database.CreateContext();
        var subject = new MediaReconciliationSubject.OriginalLayout(1);
        var claim = await AcceptAndClaimAsync(db, subject, conversion: true);
        var decision = Decision(Decoder, canEncode: true, qualified: true);
        var execution = Execution(db, decision, stopped: true);
        var demand = (await execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull();
        var facts = (await Snapshots().ReadAsync(subject)).Value;
        var capture = decision.Decide(claim.Job.Id, demand, facts).Value.ShouldBeOfType<MediaReconciliationDirective.Encode>().Capture;
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        (await execution.CaptureAsync(capture)).IsError.ShouldBeFalse();
        await using var writer = database.CreateContext();
        var error = await Should.ThrowAsync<PostgresException>(async () =>
        {
            if (changeHash)
                await writer.Files.Where(file => file.Id == 3).ExecuteUpdateAsync(set => set.SetProperty(file => file.Sha256, Romd.Domain.Hashing.Sha256.Parse(new string('f', 64))));
            else
                await writer.Files.Where(file => file.Id == 3).ExecuteUpdateAsync(set => set.SetProperty(file => file.Size, file => file.Size + 1));
        });
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(3);
        (await db.CdArchiveOwners.CountAsync()).ShouldBe(0);
        (await execution.ReadAttemptOutcomeAsync(claim.Job.Id, 1)).ShouldBeNull();
        (await execution.FinishAsync(claim.Job.Id, demand.Version, Romd.Domain.Jobs.MediaReconciliationOutcomeKind.Cancelled, "operator-cancelled", processTreeStopped: false))
            .FirstError.ShouldBe(MediaReconciliationErrors.Custody);
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(3);
    }
}
