using Romd.Admin.Application.Common.Persistence;

namespace Romd.Infrastructure.Tests.Helpers;

/// <summary>Injects failures only after the real owner's SQL, settlement or disposal has run.</summary>
internal sealed class OwnerTransactionFaults(IUnitOfWork inner, string point, Exception? primary = null,
    Func<CancellationToken, Task<bool>>? atFlush = null, Action? beforeFault = null) : IUnitOfWork
{
    private readonly string _point = point;
    public bool Injected { get; private set; }
    public bool Disposed { get; private set; }
    public bool RollbackAttempted { get; private set; }
    public Action? ObserveDisposal { get; init; }
    public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
    public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        new Transaction(this, await inner.BeginTransactionAsync(cancellationToken));
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await inner.FlushAsync(cancellationToken);
        if (_point == "flush" && !Injected && (atFlush is null || await atFlush(cancellationToken)))
            Fail();
    }
    private void Fail()
    {
        Injected = true;
        beforeFault?.Invoke();
        throw primary ?? new InvalidOperationException("No primary fault was configured.");
    }
    private sealed class Transaction(OwnerTransactionFaults owner, ITransaction inner) : ITransaction
    {
        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            if (owner._point == "commit-before")
            {
                await inner.RollbackAsync(CancellationToken.None);
                owner.Fail();
            }
            await inner.CommitAsync(cancellationToken);
            if (owner._point == "commit-after")
                owner.Fail();
        }
        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            await inner.RollbackAsync(cancellationToken);
            owner.RollbackAttempted = true;
            throw new IOException("Injected cleanup fault after real rollback.");
        }
        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            owner.Disposed = true;
            owner.ObserveDisposal?.Invoke();
            throw new IOException("Injected cleanup fault after real transaction disposal.");
        }
    }
}
