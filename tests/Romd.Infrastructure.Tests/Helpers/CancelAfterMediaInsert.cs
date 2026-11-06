using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Romd.Infrastructure.Tests.Helpers;

/// <summary>Cancels only after the target INSERT has executed and its provider reader has been released.</summary>
internal sealed class CancelAfterMediaInsert(string insertMarker, string countSql, CancellationTokenSource cancellation)
    : DbCommandInterceptor
{
    private readonly CancellationTokenSource _cancellation = cancellation;
    public OperationCanceledException Primary { get; } = new("Original cancellation after actual media INSERT", cancellation.Token);
    public int InsertedRows { get; private set; }
    public bool Reached { get; private set; }

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        DbDataReader result, CancellationToken cancellationToken = default)
    {
        if (!Reached && command.CommandText.Contains(insertMarker, StringComparison.Ordinal))
        {
            Reached = true;
            await result.DisposeAsync();
            // Sequential use of the same provider connection proves the INSERT happened inside
            // the caller's transaction, without opening an observer connection or a second context operation.
            await using var probe = command.Connection!.CreateCommand();
            probe.Transaction = command.Transaction;
            probe.CommandText = countSql;
            InsertedRows = Convert.ToInt32(await probe.ExecuteScalarAsync(CancellationToken.None));
            _cancellation.Cancel();
            throw Primary;
        }
        return result;
    }
}
