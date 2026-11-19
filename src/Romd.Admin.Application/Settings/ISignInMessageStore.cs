using ErrorOr;
using Romd.Application.Common.Concurrency;
using Romd.Contracts.Management.Models;

namespace Romd.Admin.Application.Settings;

public interface ISignInMessageStore
{
    Task<SignInMessageSettings> GetSettingsAsync(CancellationToken ct);

    Task<ErrorOr<SignInMessageSettings>> StageReplacementAsync(string message, RevisionPrecondition precondition, CancellationToken ct);
}
