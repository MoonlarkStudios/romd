using Romd.Application.Common.Pagination;
using Romd.Consumer.Application.Libraries.Queries.GetCurrentLibraryPublication;

namespace Romd.Persistence.Queries;

public sealed class ConsumerLibraryVersionReader(RomdDbContext db) : IConsumerLibraryVersionReader
{
    public async Task<string?> ReadPublicationVersionAsync(Guid userId, CancellationToken ct)
    {
        var assignment = await new LiveConsumerLibraryQuery(db).ReadAssignmentAsync(userId, ct);
        return assignment is { State: "Available" }
            ? KeysetCursor.Fingerprint("consumer.library.publication", userId, assignment.Id, assignment.Generation)
            : null;
    }
}
