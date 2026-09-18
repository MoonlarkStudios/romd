using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Users.Queries.GetUserDirectory;
using Romd.Persistence.Identity;
using Romd.Persistence.Queries;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Identity;
public sealed class UserDirectoryTests
{
    [Fact]
    public async Task Directory_UsesStableKeysetPagesAndServerFilters()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        db.Users.AddRange(Enumerable.Range(0, 63).Select(index => new RomdUser
        {
            Id = Guid.NewGuid(), Email = $"member{index:000}@example.com", NormalizedEmail = $"MEMBER{index:000}@EXAMPLE.COM",
            UserName = $"member{index:000}@example.com", NormalizedUserName = $"MEMBER{index:000}@EXAMPLE.COM",
            IsSuspended = index == 2
        }));
        await db.SaveChangesAsync();
        var reader = new UserDirectoryReader(db);
        var first = await reader.ReadAsync(new(null, null, null, null, null), default);
        first.Value.Items.Count.ShouldBe(50);
        first.Value.NextCursor.ShouldNotBeNull();
        var next = await reader.ReadAsync(new(null, null, null, null, first.Value.NextCursor), default);
        next.Value.Items.Count.ShouldBe(13);
        first.Value.Items.Select(user => user.Id).Intersect(next.Value.Items.Select(user => user.Id)).ShouldBeEmpty();
        var filtered = await reader.ReadAsync(new("MEMBER00", null, "Suspended", "none", null), default);
        filtered.Value.Items.Select(user => user.Email).ShouldBe(["member002@example.com"]);
        var badCursor = await reader.ReadAsync(new(null, null, null, null, "invalid"), default);
        badCursor.FirstError.Code.ShouldBe("Users.InvalidCursor");
    }
}
