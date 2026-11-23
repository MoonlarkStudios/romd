using System.Runtime.CompilerServices;
using Romd.Admin.Application.Titles.Enrichment;

namespace Romd.Application.Tests.Titles.Enrichment.Helpers;

/// <summary>Deterministic streaming provider; NSubstitute cannot cleanly fake IAsyncEnumerable.</summary>
internal sealed class FakeMetadataProvider(string providerId, Func<EnrichmentContext, EnrichmentResult> handler)
    : IMetadataProvider
{
    public string ProviderId { get; } = providerId;
    public string DisplayName => ProviderId;
    public bool IsConfigured { get; set; } = true;
    public bool UsesRuntimeConfiguration { get; set; }
    public int InitializeCount { get; private set; }
    public int StreamCount { get; private set; }
    public List<MetadataFetchRequest> ReceivedRequests { get; } = [];

    /// <summary>Records provider-major ordering across fakes sharing one log.</summary>
    public List<string>? CallLog { get; init; }

    public Task InitializeAsync(CancellationToken ct = default)
    {
        InitializeCount++;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<(MetadataFetchRequest Request, EnrichmentResult Result)> FetchAsync(
        IAsyncEnumerable<MetadataFetchRequest> requests,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        StreamCount++;
        await foreach (var request in requests.WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();
            ReceivedRequests.Add(request);
            CallLog?.Add($"{ProviderId}:{request.Context.TitleName}");
            yield return (request, handler(request.Context));
        }
    }
}
