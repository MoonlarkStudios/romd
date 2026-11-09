using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Users.Queries.GetUserDirectory;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Validation;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Users;
using Romd.Domain.Identity;

namespace Romd.Persistence.Queries;

public sealed class UserDirectoryReader(RomdDbContext db) : IUserDirectoryReader
{
    public async Task<ErrorOr<Page<User>>> ReadAsync(GetUserDirectoryQuery input, CancellationToken ct)
    {
        List<Error> errors = [];
        if (input.Limit is < 1 or > 100)
            errors.Add(FieldErrors.Validation("Users.InvalidLimit", "Use a limit between 1 and 100.", "limit"));
        if (input.Role is not (null or "User" or "Contributor" or "Manager" or "Admin"))
            errors.Add(FieldErrors.Validation("Users.InvalidRole", "Choose a published role.", "role"));
        if (input.Status is not (null or "Active" or "Suspended" or "Pending activation"))
            errors.Add(FieldErrors.Validation("Users.InvalidStatus", "Choose Active, Suspended or Pending activation.", "status"));
        int? libraryId = null;
        if (input.LibraryId is not (null or "none"))
        {
            if (!IdCoder.TryDecode(input.LibraryId, out int id))
                errors.Add(FieldErrors.Validation("Users.InvalidLibrary", "Use a library id or none.", "libraryId"));
            else
                libraryId = id;
        }
        string? term = input.Search?.Trim().ToUpperInvariant();
        if (term?.Length > 200)
            errors.Add(FieldErrors.Validation("Users.InvalidQuery", "Use at most 200 characters.", "query"));
        if (errors.Count > 0)
            return errors;
        string fingerprint = KeysetCursor.Fingerprint("admin.users", term, input.Role, input.Status, input.LibraryId, "email,id:asc");
        var decoded = KeysetCursor.Decode<DirectoryPosition>(input.Cursor, fingerprint, p => p.Email is not null && p.Id != Guid.Empty);
        if (decoded.IsError)
            return FieldErrors.Validation("Users.InvalidCursor", "The cursor is invalid for this query.", "cursor");
        var query = db.Users.AsNoTracking().Where(user => user.Id != SystemActor.UserId);
        if (!string.IsNullOrEmpty(term))
            query = query.Where(user => user.NormalizedEmail != null && user.NormalizedEmail.Contains(term));
        if (input.Role is not null)
            query = query.Where(user => (from membership in db.UserRoles join role in db.Roles on membership.RoleId equals role.Id where membership.UserId == user.Id && role.Name == input.Role select role.Id).Any());
        if (input.Status == "Suspended")
            query = query.Where(user => user.IsSuspended);
        else if (input.Status == "Pending activation")
            query = query.Where(user => user.RequiresActivation && !user.IsSuspended);
        else if (input.Status == "Active")
            query = query.Where(user => !user.RequiresActivation && !user.IsSuspended);
        if (input.LibraryId == "none")
            query = query.Where(user => user.LibraryId == null);
        else if (libraryId.HasValue)
            query = query.Where(user => user.LibraryId == libraryId);
        if (decoded.Value is { } position)
            query = query.Where(user => string.Compare(user.NormalizedEmail ?? "", position.Email) > 0 || (user.NormalizedEmail ?? "") == position.Email && user.Id.CompareTo(position.Id) > 0);
        var users = await query.OrderBy(user => user.NormalizedEmail ?? "").ThenBy(user => user.Id).Take(input.Limit + 1).ToListAsync(ct);
        var page = users.Take(input.Limit).ToArray();
        var ids = page.Select(user => user.Id).ToArray();
        var memberships = await (from membership in db.UserRoles join role in db.Roles on membership.RoleId equals role.Id where ids.Contains(membership.UserId) select new { membership.UserId, role.Name }).ToListAsync(ct);
        return new Page<User>
        {
            Items = page.Select(user => new User(user.Id, user.Email ?? "", memberships.Where(role => role.UserId == user.Id).Select(role => role.Name!).Order().ToArray(), user.LibraryId.HasValue ? IdCoder.Encode(user.LibraryId.Value) : null, user.CreatedAt, user.UpdatedAt, user.IsSuspended, user.RequiresActivation, user.LastSignedInAt)).ToArray(),
            HasNextPage = users.Count > input.Limit,
            NextCursor = users.Count > input.Limit ? KeysetCursor.Encode(new DirectoryPosition(page[^1].NormalizedEmail ?? "", page[^1].Id), fingerprint) : null
        };
    }
    private sealed record DirectoryPosition(string Email, Guid Id);
}
