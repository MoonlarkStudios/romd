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
    public List<EnrichmentContext> ReceivedContexts { get; } = [];

    /// <summary>Records provider-major ordering across fakes sharing one log.</summary>
    public List<string>? CallLog { get; init; }

    public Task InitializeAsync(CancellationToken ct = default)
    {
        InitializeCount++;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<(EnrichmentContext Context, EnrichmentResult Result)> EnrichAsync(
        IAsyncEnumerable<EnrichmentContext> contexts,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        StreamCount++;
        await foreach (var context in contexts.WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();
            ReceivedContexts.Add(context);
            CallLog?.Add($"{ProviderId}:{context.TitleName}");
            yield return (context, handler(context));
        }
    }
}
