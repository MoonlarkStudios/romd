using System.Data;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;

namespace Romd.Persistence.Repositories.Reconciliation;

/// <summary>Reads committed current facts and hashes actual original CUE bytes outside the snapshot transaction.</summary>
public sealed class MediaReconciliationSnapshotReader : IMediaReconciliationSnapshotReader
{
    private readonly DbContextOptions<RomdDbContext> _readOptions;
    private readonly MediaKindCatalog _kinds;
    private readonly IRomCdDescriptorIdentityReader _descriptors;
    public MediaReconciliationSnapshotReader(DbContextOptions<RomdDbContext> readOptions, MediaKindCatalog kinds, IRomCdDescriptorIdentityReader descriptors)
    {
        this._readOptions = readOptions;
        this._kinds = kinds;
        this._descriptors = descriptors;
    }
    public async Task<ErrorOr<MediaReconciliationFacts>> ReadAsync(MediaReconciliationSubject subject, CancellationToken ct = default)
    {
        await using var db = new RomdDbContext(_readOptions);
        MediaReconciliationFactRead read;
        await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct))
        {
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
            read = await new MediaReconciliationFactReader(db, _readOptions, _kinds).ReadSnapshotAsync(subject, ct);
            await transaction.CommitAsync(ct);
        }
        if (read is MediaReconciliationFactRead.Complete complete)
            return complete.Facts;
        var layout = (MediaReconciliationFactRead.Layout)read;
        RomCdFileIdentity? cue;
        try
        { cue = await _descriptors.ReadAsync(layout.CueFileId, layout.CueHash, layout.CueSize, ct); }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException or System.Text.DecoderFallbackException)
        { return new MediaReconciliationFacts.None("descriptor-ineligible"); }
        return cue is null ? MediaReconciliationErrors.Changed : layout.WithCue(cue);
    }
}
