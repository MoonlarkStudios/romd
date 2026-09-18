using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Libraries;

namespace Romd.Admin.Application.Libraries.Queries.GetLibraryExperience;

public sealed record GetLibraryAttachmentsQuery(int LibraryId) : IQuery<IReadOnlyList<LibraryAttachmentDto>>;
public sealed class GetLibraryAttachmentsQueryHandler(ILibraryRepository libraries, ILibraryExperienceRepository experience)
    : IQueryHandler<GetLibraryAttachmentsQuery, IReadOnlyList<LibraryAttachmentDto>>
{
    public async Task<ErrorOr<IReadOnlyList<LibraryAttachmentDto>>> HandleAsync(GetLibraryAttachmentsQuery query, CancellationToken ct = default)
    {
        if (await libraries.GetByIdAsync(query.LibraryId, ct) is null) return LibraryErrors.NotFound();
        return (await experience.GetAttachmentsAsync(query.LibraryId, ct)).ToList();
    }
}
