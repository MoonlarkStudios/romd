using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Common.Persistence;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;

namespace Romd.Infrastructure.Tests.Helpers;

/// <summary>Interrupts after the owner's real file-identity flush, before any dependent publication operation.</summary>
internal sealed class FileIdentityFlushInterruption(IUnitOfWork inner, RomdDbContext db, bool cancel,
    CancellationTokenSource cancellation) : IUnitOfWork
{
    public IReadOnlyList<int> GeneratedFileIds { get; private set; } = [];
    public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
    public Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) => inner.BeginTransactionAsync(cancellationToken);
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var pending = db.ChangeTracker.Entries<FileEntityPersistence>().Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity).ToArray();
        await inner.FlushAsync(cancellationToken);
        if (pending.Length == 0 || GeneratedFileIds.Count != 0)
            return;
        GeneratedFileIds = Array.AsReadOnly(pending.Select(file => file.Id).ToArray());
        GeneratedFileIds.ShouldAllBe(id => id > 0);
        (await db.Files.CountAsync(file => GeneratedFileIds.Contains(file.Id), cancellationToken)).ShouldBe(pending.Length);
        if (cancel)
        {
            await cancellation.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }
        throw new IOException("Injected failure after the owner's file identity flush.");
    }
}
