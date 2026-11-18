using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Security;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Identity;

public sealed class SignInMessageReader(RomdDbContext db) : ISignInMessageReader
{
    public Task<string> GetMessageAsync(CancellationToken ct) =>
        db.Set<SignInMessageEntity>().Select(row => row.Message).SingleAsync(ct);
}
