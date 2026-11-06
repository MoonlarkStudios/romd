using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories.Reconciliation;

/// <summary>Owns fenced attempts and durable outcomes. All native work and stop recovery precede its mutation transactions.</summary>
public sealed partial class MediaReconciliationExecution : IMediaReconciliationExecution, IMediaReconciliationPublicationStore
{
    private readonly RomdDbContext _db;
    private readonly DbContextOptions<RomdDbContext> _readOptions;
    private readonly IUnitOfWork _work;
    private readonly IMediaRunCustody _custody;
    private readonly TimeProvider _clock;
    private readonly MediaReconciliationAuthority _authority;
    private readonly MediaReconciliationDecision _decision;
    public MediaReconciliationExecution(RomdDbContext db, DbContextOptions<RomdDbContext> readOptions, IUnitOfWork work, IMediaRunCustody custody,
        TimeProvider clock, MediaKindCatalog kinds, MediaReconciliationDecision decision)
    {
        this._db = db;
        this._readOptions = readOptions;
        this._work = work;
        this._custody = custody;
        this._clock = clock;
        this._decision = decision;
        _authority = new(db, readOptions, kinds, decision);
    }
    private sealed class DemandRow
    {
        public string SubjectKind { get; set; } = null!;
        public int SubjectId { get; set; }
        public long Version { get; set; }
        public bool ConversionRequested { get; set; }
        public bool CancelRequested { get; set; }
    }
    private static async Task<MediaReconciliationDemandState?> DemandAsync(RomdDbContext context, Guid id, CancellationToken ct)
    {
        var row = await context.Database.SqlQuery<DemandRow>($"""
            SELECT definition."SubjectKind",definition."SubjectId",subject."RequestedVersion" AS "Version",subject."ConversionRequested",definition."CancelRequested"
            FROM romd."MediaReconciliationJobs" definition JOIN romd."MediaReconciliationSubjects" subject ON subject."LatestJobId"=definition."JobId"
            JOIN romd."Jobs" root ON root."Id"=definition."JobId"
            WHERE definition."JobId"={id} AND definition."RequestMode"='Subject' AND root."CompletedAt" IS NULL
            """).SingleOrDefaultAsync(ct);
        return row is null ? null : new(MediaReconciliationDefinitionEntity.Subject(row.SubjectKind, row.SubjectId), row.Version, row.ConversionRequested, row.CancelRequested);
    }
    public async Task<MediaReconciliationDemandState?> ReadDemandAsync(Guid jobId, CancellationToken ct = default)
    {
        await using var read = new RomdDbContext(_readOptions);
        return await DemandAsync(read, jobId, ct);
    }
    public async Task<MediaReconciliationJournalOutcome?> ReadOutcomeAsync(Guid jobId, CancellationToken ct = default)
    {
        await using var read = new RomdDbContext(_readOptions);
        var row = await read.Set<MediaReconciliationOutcomeEntity>().AsNoTracking().SingleOrDefaultAsync(row => row.JobId == jobId, ct);
        return row is null ? null : new(row.JobId, Enum.Parse<MediaReconciliationOutcomeKind>(row.Kind), row.Code, row.AcknowledgedVersion, new(row.CompletedAtUtcTicks, TimeSpan.Zero));
    }
    public async Task<bool> IsCancellationRequestedAsync(Guid jobId, CancellationToken ct = default)
    {
        await using var read = new RomdDbContext(_readOptions);
        return await read.Set<MediaReconciliationDefinitionEntity>().AnyAsync(row => row.JobId == jobId && row.CancelRequested, ct);
    }
    private sealed class AttemptOutcomeRow
    {
        public int Number { get; set; }
        public byte[] Fingerprint { get; set; } = null!;
        public string Outcome { get; set; } = null!;
        public string OutcomeCode { get; set; } = null!;
        public int? ResultRepresentationId { get; set; }
        public Guid? ResultVerificationId { get; set; }
        public Guid? ResultCreationId { get; set; }
        public Guid? ResultInspectionId { get; set; }
        public long CompletedAtUtcTicks { get; set; }
    }
    public async Task<MediaReconciliationAttemptOutcome?> ReadAttemptOutcomeAsync(Guid jobId, int number, CancellationToken ct = default)
    {
        await using var read = new RomdDbContext(_readOptions);
        var row = await read.Database.SqlQuery<AttemptOutcomeRow>($"""
            SELECT "Number","Fingerprint","Outcome","OutcomeCode","ResultRepresentationId","ResultVerificationId","ResultCreationId","ResultInspectionId","CompletedAtUtcTicks"
            FROM romd."MediaReconciliationAttempts" WHERE "JobId"={jobId} AND "Number"={number} AND "Outcome" IS NOT NULL
            """).SingleOrDefaultAsync(ct);
        return row is null ? null : new(jobId, row.Number, Romd.Domain.Hashing.Sha256.FromSpan(row.Fingerprint), Enum.Parse<MediaReconciliationAttemptDisposition>(row.Outcome),
            row.OutcomeCode, row.ResultRepresentationId, row.ResultVerificationId, row.ResultCreationId, row.ResultInspectionId, new(row.CompletedAtUtcTicks, TimeSpan.Zero));
    }
    private static Guid Fence(Guid jobId) => JobExecutionFenceScope.Current is { } fence && fence.JobId == jobId
        ? fence.Token : throw new JobExecutionOwnershipLostException();
    private void RequireOwnedTransaction(Guid jobId)
    {
        Guid token = Fence(jobId);
        if (_db.LockedJobExecution is not { } locked || locked.JobId != jobId || locked.Token != token ||
            _db.Database.CurrentTransaction?.TransactionId != locked.TransactionId)
            throw new JobExecutionOwnershipLostException();
    }
    private static void CheckCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 128 || code.Any(char.IsControl))
            throw new ArgumentException("A bounded durable outcome code is required.", nameof(code));
    }
}
