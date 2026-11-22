using System.Data;
using Microsoft.EntityFrameworkCore;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Libraries;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Queries;

public sealed class LiveConsumerLibraryQuery(RomdDbContext context)
{
    internal async Task<LiveLibraryAssignment?> ReadAssignmentAsync(Guid userId, CancellationToken ct)
    {
        var row = await (from user in context.Users.AsNoTracking()
                         where user.Id == userId && user.LibraryId != null
                         join library in context.Libraries.AsNoTracking() on user.LibraryId equals library.Id
                         select new
                         {
                             library.Id,
                             library.Name,
                             library.ConfigurationJson,
                             library.ConfigurationState,
                             library.ConfigurationError,
                             library.NeedsMaterialization,
                             library.LastMaterializedAt,
                             library.MaterializationGeneration
                         }).SingleOrDefaultAsync(ct);
        if (row is null)
            return null;
        var config = LibraryEntity.DeserializeConfig(row.ConfigurationJson, row.ConfigurationState, row.ConfigurationError);
        var state = config.State != LibraryConfigurationState.Valid ? "InvalidRules"
            : row.NeedsMaterialization && row.LastMaterializedAt is null ? "NeedsPublication" : "Available";
        return new(row.Id, row.Name, state, row.LastMaterializedAt, row.MaterializationGeneration);
    }

    internal sealed record LiveLibraryAssignment(int Id, string Name, string State, DateTimeOffset? PublishedAt, long Generation);

    public async Task<ConsumerLibraryReadResult<T>> ReadAsync<T>(
        ConsumerLibraryScope scope,
        Func<int, CancellationToken, Task<ConsumerLibraryProjectionResult<T>>> readProjection,
        CancellationToken ct = default)
        where T : notnull
    {
        // One REPEATABLE READ snapshot covers the library lookup and the projection read, so a
        // concurrent materialization cannot change the library between the two.
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);

        var liveLibrary = await ReadAssignmentAsync(scope.UserId, ct);
        if (liveLibrary is not { State: "Available" })
        {
            await transaction.CommitAsync(ct);
            return new ConsumerLibraryReadResult<T>.LibraryUnavailable();
        }

        var projection = await readProjection(liveLibrary.Id, ct);
        await transaction.CommitAsync(ct);
        return projection switch
        {
            ConsumerLibraryProjectionResult<T>.Found found =>
                new ConsumerLibraryReadResult<T>.Found(liveLibrary.Id, found.Value),
            ConsumerLibraryProjectionResult<T>.ItemNotFound =>
                new ConsumerLibraryReadResult<T>.ItemNotFound(liveLibrary.Id),
            ConsumerLibraryProjectionResult<T>.Rejected rejected =>
                new ConsumerLibraryReadResult<T>.Rejected(rejected.Error),
            ConsumerLibraryProjectionResult<T>.ProjectionInconsistent =>
                new ConsumerLibraryReadResult<T>.ProjectionInconsistent(),
            _ => throw new InvalidOperationException("Unknown Consumer Library projection result.")
        };
    }
}
