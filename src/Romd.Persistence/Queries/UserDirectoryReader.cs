using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Users.Queries.GetUserDirectory;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Users;
using Romd.Domain.Identity;

namespace Romd.Persistence.Queries;
public sealed class UserDirectoryReader(RomdDbContext db) : IUserDirectoryReader
{
    public async Task<ErrorOr<UserDirectoryPageDto>> ReadAsync(GetUserDirectoryQuery input, CancellationToken ct)
    {
        var query = db.Users.AsNoTracking().Where(user => user.Id != SystemActor.UserId);
        if (!string.IsNullOrWhiteSpace(input.Search))
        {
            string term = input.Search.Trim().ToUpperInvariant();
            query = query.Where(user => user.NormalizedEmail != null && user.NormalizedEmail.Contains(term));
        }
        if (!string.IsNullOrEmpty(input.Role))
            query = query.Where(user => (from membership in db.UserRoles
                join role in db.Roles on membership.RoleId equals role.Id
                where membership.UserId == user.Id && role.Name == input.Role select role.Id).Any());
        if (input.Status == "Suspended") query = query.Where(user => user.IsSuspended);
        else if (input.Status == "Pending activation") query = query.Where(user => user.RequiresActivation && !user.IsSuspended);
        else if (input.Status == "Active") query = query.Where(user => !user.RequiresActivation && !user.IsSuspended);
        if (input.LibraryId == "none") query = query.Where(user => user.LibraryId == null);
        else if (!string.IsNullOrEmpty(input.LibraryId))
        {
            if (!IdCoder.TryDecode(input.LibraryId, out int libraryId)) return Error.Validation("Users.InvalidLibrary", "Invalid library filter.");
            query = query.Where(user => user.LibraryId == libraryId);
        }
        if (!string.IsNullOrEmpty(input.Cursor))
        {
            try
            {
                var cursor = JsonSerializer.Deserialize<DirectoryCursor>(Convert.FromBase64String(input.Cursor));
                if (cursor is null) return InvalidCursor();
                query = query.Where(user => string.Compare(user.NormalizedEmail, cursor.Email) > 0 ||
                    (user.NormalizedEmail == cursor.Email && user.Id.CompareTo(cursor.Id) > 0));
            }
            catch (Exception ex) when (ex is FormatException or JsonException) { return InvalidCursor(); }
        }
        var users = await query.OrderBy(user => user.NormalizedEmail).ThenBy(user => user.Id).Take(51).ToListAsync(ct);
        var ids = users.Take(50).Select(user => user.Id).ToArray();
        var memberships = await (from membership in db.UserRoles
            join role in db.Roles on membership.RoleId equals role.Id
            where ids.Contains(membership.UserId)
            select new { membership.UserId, role.Name }).ToListAsync(ct);
        var items = users.Take(50).Select(user => new UserDto(user.Id, user.Email ?? "",
            memberships.Where(role => role.UserId == user.Id).Select(role => role.Name!).Order().ToList(),
            user.LibraryId.HasValue ? IdCoder.Encode(user.LibraryId.Value) : null,
            user.CreatedAt, user.UpdatedAt, user.IsSuspended, user.RequiresActivation, user.LastSignedInAt)).ToList();
        string? next = users.Count > 50 ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new DirectoryCursor(users[49].NormalizedEmail ?? "", users[49].Id))) : null;
        return new UserDirectoryPageDto(items, next);
    }
    private sealed record DirectoryCursor(string Email, Guid Id);
    private static Error InvalidCursor() => Error.Validation("Users.InvalidCursor", "Invalid directory cursor. Return to the first page.");
}
