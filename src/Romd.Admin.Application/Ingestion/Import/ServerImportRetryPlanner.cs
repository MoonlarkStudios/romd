using System.Security.Cryptography;
using System.Text.Json;
using ErrorOr;
using Romd.Contracts.Management.Models;

namespace Romd.Admin.Application.Ingestion.Import;

public sealed record ServerImportRetryPlan(Guid CorrelationId, ServerImportRetryReview Review, IReadOnlyList<ServerImportFile> Candidates);

public sealed class ServerImportRetryPlanner(IServerImportStore store, IServerImportSource source, TimeProvider clock)
{
    private const int _exampleLimit = 12;

    public async Task<ErrorOr<ServerImportRetryPlan>> ReviewAsync(
        string directory, Guid jobId, ServerImportJob? latest, CancellationToken ct)
    {
        var batch = await store.ReadRetryBatchAsync(directory, jobId, ct);
        if (batch is null)
            return ImportErrors.RetryUnavailable();

        var observation = source.ObserveRetry(directory, batch.Selection, clock.GetUtcNow(), ct);
        if (observation.IsError)
            return observation.Errors;

        var observed = observation.Value.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
        bool busy = latest is { IsTerminal: false };
        // Bind the whole observation, not just the files eligible to copy. Advancing the
        // folder's accepted job also invalidates a review, even after a fast retry finishes.
        string revision = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1,
            JobId = jobId,
            LatestJobId = latest?.Id,
            Busy = busy,
            Files = observed
        })));
        var groups = observed.ToLookup(file => file.State);
        var candidates = groups[ServerImportRetryFileState.Ready].Select(file =>
            file.Current ?? throw new InvalidOperationException("A ready retry file must have a version.")).ToArray();
        var review = new ServerImportRetryReview(revision, busy,
            Summarize(ServerImportRetryFileState.Ready), Summarize(ServerImportRetryFileState.Changed),
            Summarize(ServerImportRetryFileState.Missing), Summarize(ServerImportRetryFileState.Unavailable));
        return new ServerImportRetryPlan(batch.CorrelationId, review, candidates);

        ServerImportRetryFileGroup Summarize(ServerImportRetryFileState state) =>
            new(groups[state].Count(), groups[state].Take(_exampleLimit).Select(file => file.RelativePath).ToArray());
    }
}
