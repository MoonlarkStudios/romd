using Romd.Application.Common.Security;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
namespace Romd.Admin.Application.Ingestion.Jobs;

public sealed record JobVisibilityScope(Guid? OwnerId, bool IncludeAdminOnly, bool Empty);

/// <summary>One visibility scope for collections, resources and mutations; absence never grants access.</summary>
public static class JobVisibility
{
    private static readonly IReadOnlyDictionary<Type, string> _adminOnlyFamilies = new Dictionary<Type, string>
    {
        [typeof(MediaReconciliationJob)] = "media-reconciliation",
    };
    public static readonly string[] AdminOnlyJobTypes = _adminOnlyFamilies.Values.ToArray();
    public static JobVisibilityScope ScopeFor(ICurrentUser user) => user.UserId is not { } actor || actor == Guid.Empty
        ? new(null, false, true)
        : new(user.HasRole(RomdRoleType.Manager) ? null : actor, user.HasRole(RomdRoleType.Admin), false);
    public static bool IsVisibleTo(Job job, JobVisibilityScope scope) => !scope.Empty &&
        (!_adminOnlyFamilies.ContainsKey(job.GetType()) || scope.IncludeAdminOnly) &&
        (scope.OwnerId is null || job.CreatedByUserId == scope.OwnerId);
    public static async Task<Job?> GetVisibleAsync(this IJobRepository jobs, Guid jobId, ICurrentUser user, CancellationToken ct = default)
    {
        var scope = ScopeFor(user);
        return !scope.Empty && await jobs.GetByIdAsync(jobId, ct) is { } job && IsVisibleTo(job, scope) ? job : null;
    }
}
