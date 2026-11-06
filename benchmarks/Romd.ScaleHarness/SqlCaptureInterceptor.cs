using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Romd.ScaleHarness;

public sealed record CapturedParameter(string Name, object? Value);

public sealed record CapturedCommand(string Text, IReadOnlyList<CapturedParameter> Parameters);

/// <summary>
///     Harness-only <see cref="DbCommandInterceptor" /> that records the SQL (and bound
///     parameters) EF Core actually executes, so each scenario's statements can be replayed
///     under EXPLAIN. Never registered in production hosts.
/// </summary>
public sealed class SqlCaptureInterceptor : DbCommandInterceptor
{
    private readonly Lock _gate = new();
    private readonly List<CapturedCommand> _captured = [];
    private volatile bool _capturing;

    public void Begin()
    {
        lock (_gate)
        {
            _captured.Clear();
            _capturing = true;
        }
    }

    public IReadOnlyList<CapturedCommand> End()
    {
        lock (_gate)
        {
            _capturing = false;
            return _captured.ToList();
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Record(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Record(command);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void Record(DbCommand command)
    {
        if (!_capturing)
        {
            return;
        }

        var parameters = command.Parameters
            .Cast<DbParameter>()
            .Select(p => new CapturedParameter(p.ParameterName, SnapshotValue(p.Value)))
            .ToList();

        lock (_gate)
        {
            if (_capturing)
            {
                _captured.Add(new CapturedCommand(command.CommandText, parameters));
            }
        }
    }

    private static object? SnapshotValue(object? value) =>
        value is byte[] bytes ? bytes.ToArray() : value;
}
