using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Romd.Infrastructure.Tests.Helpers;

/// <summary>The text of every command a context sends, in order.</summary>
internal sealed class CommandLog : DbCommandInterceptor
{
    private readonly List<string> commands = [];

    public IReadOnlyList<string> Commands
    {
        get { lock (commands) return commands.ToArray(); }
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void Record(DbCommand command)
    {
        lock (commands)
            commands.Add(command.CommandText);
    }
}
