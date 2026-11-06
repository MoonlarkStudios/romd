using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Libraries.Commands.SetLibraryAttachments;

public sealed record SetLibraryAttachmentsCommand(int LibraryId, IReadOnlyList<(int CollectionId, bool IsFeatured)> Collections) : ICommand<Success>;
public sealed class SetLibraryAttachmentsCommandHandler(ILibraryExperienceRepository experience, IUnitOfWork unitOfWork,
    ILogger<SetLibraryAttachmentsCommandHandler> logger)
    : ICommandHandler<SetLibraryAttachmentsCommand, Success>
{
    public async Task<ErrorOr<Success>> HandleAsync(SetLibraryAttachmentsCommand command, CancellationToken ct = default)
    {
        return await unitOfWork.ExecuteInTransactionAsync<Success>(
            token => experience.SetAttachmentsAsync(command.LibraryId, command.Collections, token), logger, ct);
    }
}
