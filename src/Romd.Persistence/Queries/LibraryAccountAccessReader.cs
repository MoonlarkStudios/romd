using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Users;
using Romd.Admin.Application.Users.Queries.GetLibraryAccountAccess;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Users;
using Romd.Domain.Identity;

namespace Romd.Persistence.Queries;

public sealed class LibraryAccountAccessReader(RomdDbContext db) : ILibraryAccountAccessReader
{
    public async Task<ErrorOr<LibraryAccountAccess>> ReadAsync(Guid userId, int? titleId, CancellationToken ct)
    {
        var user = await db.Users.Where(user => user.Id == userId && user.Id != SystemActor.UserId)
            .Select(user => new { user.IsSuspended, user.RequiresActivation }).SingleOrDefaultAsync(ct);
        if (user is null)
            return UserErrors.NotFound();

        var assignment = await new LiveConsumerLibraryQuery(db).ReadAssignmentAsync(userId, ct);
        var state = user.IsSuspended ? "Suspended" : user.RequiresActivation ? "ActivationRequired" : assignment?.State ?? "Unassigned";
        string? libraryId = assignment is null ? null : IdCoder.Encode(assignment.Id);
        if (state != "Available" || assignment is null)
            return new LibraryAccountAccess(state, libraryId, assignment?.Name, assignment?.PublishedAt, null, null);

        var games = db.MaterializedLibraryTitles.Where(game => game.LibraryId == assignment.Id && game.IsOwned);
        var count = await games.CountAsync(ct);
        bool? visible = titleId.HasValue ? await games.AnyAsync(game => game.TitleId == titleId, ct) : null;
        return new LibraryAccountAccess(state, libraryId, assignment.Name, assignment.PublishedAt, count, visible);
    }
}
