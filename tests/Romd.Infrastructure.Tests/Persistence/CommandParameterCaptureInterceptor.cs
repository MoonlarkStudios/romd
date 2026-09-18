using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Romd.Infrastructure.Tests.Persistence;

internal sealed record CapturedReaderCommand(string CommandText, IReadOnlyList<string> ParameterNames)
{
    public int ParameterCount => ParameterNames.Count;
}

internal sealed class CommandParameterCaptureInterceptor : DbCommandInterceptor
{
    public List<CapturedReaderCommand> Commands { get; } = [];

    public void Clear() => Commands.Clear();

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Capture(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Capture(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void Capture(DbCommand command) => Commands.Add(new CapturedReaderCommand(
        command.CommandText,
        command.Parameters.Cast<DbParameter>().Select(parameter => parameter.ParameterName).ToList()));
}
