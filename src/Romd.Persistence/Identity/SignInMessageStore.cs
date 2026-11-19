using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Settings;
using Romd.Application.Common.Concurrency;
using Romd.Contracts.Management.Models;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Identity;

public sealed class SignInMessageStore(RomdDbContext db) : ISignInMessageStore
{
    public Task<SignInMessageSettings> GetSettingsAsync(CancellationToken ct) =>
        db.Set<SignInMessageEntity>().Select(row => new SignInMessageSettings(row.Revision, row.Message)).SingleAsync(ct);

    public async Task<ErrorOr<SignInMessageSettings>> StageReplacementAsync(
        string message, RevisionPrecondition precondition, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Sign-in message writes require a caller-owned transaction.");

        var currentRevision = await db.Set<SignInMessageEntity>().Select(row => row.Revision).SingleAsync(ct);
        if (!precondition.IsSatisfiedBy(currentRevision))
            return PreconditionFailed();

        var newRevision = Guid.NewGuid();
        int changed = await db.Set<SignInMessageEntity>()
            .Where(row => row.Id == 1 && row.Revision == currentRevision)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Message, message)
                .SetProperty(row => row.Revision, newRevision), ct);

        return changed == 1 ? new SignInMessageSettings(newRevision, message) : PreconditionFailed();
    }

    private static Error PreconditionFailed() =>
        Error.Custom(412, "SignInMessage.PreconditionFailed", "The sign-in message changed. Reload the saved message before saving again.");
}
