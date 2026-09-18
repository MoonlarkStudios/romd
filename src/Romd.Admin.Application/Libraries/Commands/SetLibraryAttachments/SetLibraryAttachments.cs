using ErrorOr;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Libraries.Commands.SetLibraryAttachments;

public sealed record SetLibraryAttachmentsCommand(int LibraryId, IReadOnlyList<(int CollectionId, bool IsFeatured)> Collections) : ICommand<Success>;
public sealed class SetLibraryAttachmentsCommandHandler(ILibraryExperienceRepository experience, IUnitOfWork unitOfWork)
    : ICommandHandler<SetLibraryAttachmentsCommand, Success>
{
    public async Task<ErrorOr<Success>> HandleAsync(SetLibraryAttachmentsCommand command, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        var result = await experience.SetAttachmentsAsync(command.LibraryId, command.Collections, ct);
        if (result.IsError) return result;
        await transaction.CommitAsync(ct);
        return result;
    }
}
