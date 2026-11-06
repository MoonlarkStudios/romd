using Romd.Contracts.Management.Artwork;

namespace Romd.Admin.Application.Artwork;

/// <summary>Chooses the first compatible active owner for each title, retaining the query's existing preference order.</summary>
public static class ArtworkCompletionSharing
{
    public static IReadOnlyList<ArtworkCompletionPlannedItem> Plan(ArtworkCompletionRequest request,
        ArtworkCompletionScopeSnapshot scope, IReadOnlyList<ArtworkCompletionOwner> orderedOwners)
    {
        bool RequiresTracking(int id) => scope.TrackedRetryIds.Contains(id) || request.Scope is "AllTracked" or "Automatic" ||
            request.Scope == "Filtered" && request.Filters?.Tracked.Equals("tracked", StringComparison.OrdinalIgnoreCase) == true;
        var owners = new Dictionary<int, Guid>();
        foreach (var owner in orderedOwners)
            if ((owner.Mode == request.Mode || owner.Mode == "Enrich") && (RequiresTracking(owner.TitleId) || !owner.RequireTracked))
                owners.TryAdd(owner.TitleId, owner.JobId);
        return Array.AsReadOnly(scope.Titles.Select(title => new ArtworkCompletionPlannedItem(title.TitleId, title.TitleName,
            RequiresTracking(title.TitleId), owners.TryGetValue(title.TitleId, out var owner) ? owner : null)).ToArray());
    }
}
