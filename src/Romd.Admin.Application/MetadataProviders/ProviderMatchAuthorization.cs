namespace Romd.Admin.Application.MetadataProviders;

/// <summary>Authorizes one provider game at the revision supported by the current evidence.</summary>
public sealed record ProviderMatchAuthorization(string ProviderId, string GameId, Guid Revision, bool IsProvisional = false);

public enum ProviderMatchBlock { ProviderDisabled, ProviderUnconfigured, Unlinked, Failed, NeedsMatch }

public abstract record ProviderMatchAuthorizationResult
{
    private ProviderMatchAuthorizationResult() { }
    public sealed record Authorized(ProviderMatchAuthorization Match) : ProviderMatchAuthorizationResult;
    public sealed record Blocked(ProviderMatchBlock Reason) : ProviderMatchAuthorizationResult;
}

public interface IProviderMatchAuthorizationStore
{
    Task<ProviderMatchAuthorizationResult?> ReadAuthorizationAsync(int titleId, string providerId, CancellationToken ct);
}
