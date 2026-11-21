using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Contracts.Consumer.Libraries;

namespace Romd.Consumer.Application.Libraries.Queries.GetCurrentLibraryPublication;

public sealed record GetCurrentLibraryPublicationQuery : IQuery<LibraryPublicationVersion>;

public interface IConsumerLibraryVersionReader
{
    Task<string?> ReadPublicationVersionAsync(Guid userId, CancellationToken ct);
}

public sealed class GetCurrentLibraryPublicationQueryHandler(ICurrentUser currentUser, IConsumerLibraryVersionReader reader)
    : IQueryHandler<GetCurrentLibraryPublicationQuery, LibraryPublicationVersion>
{
    public async Task<ErrorOr<LibraryPublicationVersion>> HandleAsync(GetCurrentLibraryPublicationQuery query, CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
            return ConsumerErrors.CurrentUserRequired();
        return new LibraryPublicationVersion(await reader.ReadPublicationVersionAsync(userId, ct));
    }
}
