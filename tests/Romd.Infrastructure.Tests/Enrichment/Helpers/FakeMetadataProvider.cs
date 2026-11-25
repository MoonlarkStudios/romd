using System.Runtime.CompilerServices;
using Romd.Admin.Application.Titles.Enrichment;

namespace Romd.Infrastructure.Tests.Enrichment.Helpers;

internal sealed class FakeMetadataProvider : IMetadataProvider
{
    private readonly Func<EnrichmentContext, EnrichmentResult> _handler;

    public string ProviderId { get; }
    public string DisplayName => ProviderId;
    public bool IsConfigured { get; set; } = true;
    public List<MetadataFetchRequest> ReceivedRequests { get; } = [];

    public FakeMetadataProvider(string providerId, Func<EnrichmentContext, EnrichmentResult> handler)
    {
        ProviderId = providerId;
        _handler = handler;
    }

    public async IAsyncEnumerable<(MetadataFetchRequest, EnrichmentResult)> FetchAsync(
        IAsyncEnumerable<MetadataFetchRequest> requests,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var request in requests.WithCancellation(ct))
        {
            ReceivedRequests.Add(request);
            yield return (request, _handler(request.Context));
        }
    }
}
