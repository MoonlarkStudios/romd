using ErrorOr;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Admin.Application.Ingestion.Jobs;

namespace Romd.Admin.Application.MetadataProviders;

public sealed record ProviderAvailability(bool Enabled, bool Configured);

public interface IProviderIdentityAdapter
{
    string Id { get; }
    string Name { get; }
    IReadOnlyList<string> Capabilities { get; }
    Task<ProviderAvailability> GetAvailabilityAsync(CancellationToken ct);
    Task<ErrorOr<IReadOnlyList<ProviderGameDto>>> SearchAsync(string query, CancellationToken ct);
    Task<ErrorOr<ProviderGameDto>> ResolveAsync(string idOrUrl, CancellationToken ct);
}

public interface ITitleProviderMatchService
{
    Task DiscoverAsync(int titleId, JobContext context, CancellationToken ct);
    Task<bool> IsSuppressedAsync(int titleId, string providerId, CancellationToken ct);
    Task<Guid?> GetLinkRevisionAsync(int titleId, string providerId, string gameId, CancellationToken ct);
    Task<ErrorOr<IReadOnlyList<TitleProviderMatchDto>>> GetAsync(int titleId, CancellationToken ct);
    Task<ErrorOr<IReadOnlyList<ProviderGameDto>>> SearchAsync(string providerId, ProviderMatchSearchRequest request, CancellationToken ct);
    Task<ErrorOr<Success>> SetAsync(int titleId, string providerId, SetProviderMatchRequest request, CancellationToken ct);
    Task<ErrorOr<Success>> ConfirmAsync(int titleId, string providerId, Guid revision, CancellationToken ct);
    Task<ErrorOr<Success>> UnlinkAsync(int titleId, string providerId, Guid revision, CancellationToken ct);
}

public static class ProviderMatchErrors
{
    public static Error InvalidInput => Error.Validation("ProviderMatch.InvalidInput", "Enter a valid provider game ID or page URL.");
    public static Error Unavailable => Error.Failure("ProviderMatch.Unavailable", "The provider is unavailable. Try again later.");
    public static Error NotFound => Error.NotFound("ProviderMatch.NotFound", "The provider game was not found.");
    public static Error Conflict => Error.Conflict("ProviderMatch.Conflict", "The provider match changed. Reload before trying again.");
}
