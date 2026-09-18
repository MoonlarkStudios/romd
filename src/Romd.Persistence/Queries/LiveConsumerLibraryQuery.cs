using System.Data;
using Microsoft.EntityFrameworkCore;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Libraries;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Queries;

public sealed class LiveConsumerLibraryQuery(RomdDbContext context)
{
    public async Task<ConsumerLibraryReadResult<T>> ReadAsync<T>(
        ConsumerLibraryScope scope,
        Func<int, CancellationToken, Task<ConsumerLibraryProjectionResult<T>>> readProjection,
        CancellationToken ct = default)
        where T : notnull
    {
        // One REPEATABLE READ snapshot covers the library lookup and the projection read, so a
        // concurrent materialization cannot change the library between the two.
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);

        var liveLibrary = await (
                from user in context.Users.AsNoTracking()
                where user.Id == scope.UserId && user.LibraryId != null
                join libraryEntity in context.Libraries.AsNoTracking()
                    on user.LibraryId equals libraryEntity.Id
                select new
                {
                    libraryEntity.Id,
                    libraryEntity.ConfigurationJson,
                    libraryEntity.ConfigurationState,
                    libraryEntity.ConfigurationError,
                    libraryEntity.NeedsMaterialization
                })
            .SingleOrDefaultAsync(ct);

        var configuration = liveLibrary is null
            ? null
            : LibraryEntity.DeserializeConfig(
                liveLibrary.ConfigurationJson,
                liveLibrary.ConfigurationState,
                liveLibrary.ConfigurationError);
        if (liveLibrary is null ||
            liveLibrary.NeedsMaterialization ||
            configuration?.State != LibraryConfigurationState.Valid)
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
            ConsumerLibraryProjectionResult<T>.ProjectionInconsistent =>
                new ConsumerLibraryReadResult<T>.ProjectionInconsistent(),
            _ => throw new InvalidOperationException("Unknown Consumer Library projection result.")
        };
    }
}
