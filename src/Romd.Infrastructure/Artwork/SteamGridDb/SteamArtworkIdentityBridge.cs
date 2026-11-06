using ErrorOr;
using Romd.Admin.Application.MetadataProviders;
using Romd.Domain.Catalog;
using Romd.Infrastructure.Enrichment;

namespace Romd.Infrastructure.Artwork.SteamGridDb;

public sealed class SteamArtworkIdentityBridge(IgdbMetadataProvider igdb, SteamGridDbArtworkProvider steamGridDb)
    : IArtworkIdentityBridge
{
    public string SourceProviderId => "igdb";
    public string TargetProviderId => "steamgriddb";

    public async Task<ErrorOr<ArtworkBridgeMatch?>> ResolveAsync(string sourceGameId, CancellationToken ct)
    {
        var source = await igdb.GetSteamIdentityAsync(sourceGameId, ct);
        if (source.IsError)
            return source.Errors;
        if (source.Value is null)
            return (ArtworkBridgeMatch?)null;
        var game = await steamGridDb.ResolveSteamIdentityAsync(source.Value.Value.AppId, ct);
        if (game.IsError)
            return game.Errors;
        // An external store listing can point at a remaster or bundle. Require its
        // provider name to retain the source game's edition identity as well.
        if (ArtworkIdentityMatcher.Normalize(source.Value.Value.Name) != ArtworkIdentityMatcher.Normalize(game.Value.Name))
            return (ArtworkBridgeMatch?)null;
        return new ArtworkBridgeMatch(game.Value,
            $"IGDB game {sourceGameId} → Steam app {source.Value.Value.AppId} → SteamGridDB game {game.Value.Id}; matching edition name.");
    }
}
