using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Storage;
using Romd.Persistence;
using Romd.Persistence.Repositories;

namespace Romd.PostgreSql.TestSupport;

/// <summary>Explicit setup owners for test files. The chosen method states who owns the transaction.</summary>
public static class FilePublicationTestOwner
{
    public static async Task<FileStoreResult> PublishAndFlushAsync(this IFileStorageService storage,
        RomdDbContext db, IStagedFile staged, CancellationToken ct = default)
    {
        RequireTransaction(db);
        var publication = await storage.PublishStagedAsync(staged, ct);
        if (publication.RequiresFlush)
            await new EfUnitOfWork(db).FlushAsync(ct);
        return publication.ReadAfterFlush();
    }

    public static async Task<IReadOnlyList<FileStoreResult>> PublishAndFlushAsync(this IFileStorageService storage,
        RomdDbContext db, IReadOnlyList<IStagedFile> staged, CancellationToken ct = default)
    {
        RequireTransaction(db);
        var publication = await storage.PublishStagedAsync(staged, ct);
        if (publication.RequiresFlush)
            await new EfUnitOfWork(db).FlushAsync(ct);
        return publication.ReadAfterFlush();
    }

    public static async Task<FileStoreResult> StageAndPublishAsync(this IFileStorageService storage,
        RomdDbContext db, Stream source, CancellationToken ct = default)
    {
        RequireTransaction(db);
        await using var staged = await storage.StageAsync(source, ct);
        return await storage.PublishAndFlushAsync(db, staged, ct);
    }

    public static async Task<FileStoreResult> StageAndCommitAsync(this IFileStorageService storage,
        RomdDbContext db, Stream source, CancellationToken ct = default)
    {
        RequireNewTransaction(db);
        await using var staged = await storage.StageAsync(source, ct);
        await using var transaction = await new EfUnitOfWork(db).BeginTransactionAsync(ct);
        var result = await storage.PublishAndFlushAsync(db, staged, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    public static async Task<FileEntity> CreateAndFlushAsync(this IFileRepository files,
        RomdDbContext db, FileEntity file, CancellationToken ct = default)
    {
        RequireTransaction(db);
        await new FileMutationLock(db).AcquireAsync(file.Sha256, ct);
        var creation = await files.AddStagedAsync(file, ct);
        if (creation.RequiresFlush)
            await new EfUnitOfWork(db).FlushAsync(ct);
        return creation.ReadAfterFlush();
    }

    public static async Task<FileEntity> CreateAndCommitAsync(this IFileRepository files,
        RomdDbContext db, FileEntity file, CancellationToken ct = default)
    {
        RequireNewTransaction(db);
        await using var transaction = await new EfUnitOfWork(db).BeginTransactionAsync(ct);
        var result = await files.CreateAndFlushAsync(db, file, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    private static void RequireTransaction(RomdDbContext db)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("This test setup operation requires an explicit caller-owned transaction.");
    }

    private static void RequireNewTransaction(RomdDbContext db)
    {
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("This test setup owner must not commit another owner's transaction.");
    }
}
