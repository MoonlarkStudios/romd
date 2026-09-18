using System.Runtime.CompilerServices;
using Romd.Admin.Application.Titles.Enrichment;

namespace Romd.Infrastructure.Tests.Enrichment.Helpers;

internal sealed class FakeMetadataProvider : IMetadataProvider
{
    private readonly Func<EnrichmentContext, EnrichmentResult> _handler;

    public string ProviderId { get; }
    public string DisplayName => ProviderId;
    public bool IsConfigured { get; set; } = true;
    public List<EnrichmentContext> ReceivedContexts { get; } = [];

    public FakeMetadataProvider(string providerId, Func<EnrichmentContext, EnrichmentResult> handler)
    {
        ProviderId = providerId;
        _handler = handler;
    }

    public async IAsyncEnumerable<(EnrichmentContext, EnrichmentResult)> EnrichAsync(
        IAsyncEnumerable<EnrichmentContext> contexts,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var ctx in contexts.WithCancellation(ct))
        {
            ReceivedContexts.Add(ctx);
            yield return (ctx, _handler(ctx));
        }
    }
}
