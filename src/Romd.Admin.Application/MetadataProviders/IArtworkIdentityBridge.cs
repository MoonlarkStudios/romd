using ErrorOr;

namespace Romd.Admin.Application.MetadataProviders;

public sealed record ArtworkBridgeMatch(ProviderGame Game, string Evidence);
public interface IArtworkIdentityBridge
{
    string SourceProviderId { get; }
    string TargetProviderId { get; }
    Task<ErrorOr<ArtworkBridgeMatch?>> ResolveAsync(string sourceGameId, CancellationToken ct);
}
