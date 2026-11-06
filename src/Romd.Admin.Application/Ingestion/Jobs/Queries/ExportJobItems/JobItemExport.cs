using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Common.Persistence;

namespace Romd.Admin.Application.Ingestion.Jobs.Queries.ExportJobItems;

/// <summary>
///     A single-pass, bounded export and its live snapshot. Completing the read does not commit:
///     the response must explicitly complete after flushing the entire representation.
/// </summary>
public sealed class JobItemExport(
    IJobItemRepository repository,
    IReadSnapshotTransaction transaction,
    Guid jobId,
    JobItemPageResult firstPage, ILogger? logger = null) : IAsyncDisposable
{
    public const int BatchSize = 1000;
    private bool _readStarted;
    private bool _readFinished;
    private bool _completed;
    private bool _disposed;
    private Exception? _failure;

    public async IAsyncEnumerable<JobItemView> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_readStarted)
            throw new InvalidOperationException("An item export can only be consumed once.");
        _readStarted = true;

        var page = firstPage;
        while (true)
        {
            foreach (var item in page.Items)
            {
                try
                { ct.ThrowIfCancellationRequested(); }
                catch (OperationCanceledException primary) { _failure = primary; throw; }
                yield return item;
            }

            if (!page.HasMore)
                break;
            if (page.NextPosition is null || page.Items.Count == 0)
            {
                _failure = new InvalidOperationException("The item export page has no continuation position.");
                throw _failure;
            }
            try
            { page = await repository.GetByJobAsync(jobId, null, BatchSize, ct, page.NextPosition); }
            catch (Exception primary) { _failure = primary; throw; }
        }

        _readFinished = true;
    }

    public async Task CompleteAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_readFinished)
            throw new InvalidOperationException("The entire export must be consumed before completion.");
        if (_completed)
            return;
        try
        {
            await transaction.CompleteAsync(ct);
            _completed = true;
        }
        catch (Exception primary) { _failure = primary; throw; }
    }

    public ValueTask DisposeAsync() => DisposePreservingFailureAsync(_failure);

    /// <summary>Releases the snapshot after a response/preparation failure without replacing that failure.</summary>
    public async ValueTask DisposePreservingFailureAsync(Exception? primary)
    {
        if (_disposed)
            return;
        _disposed = true;
        await ReadSnapshotCleanup.DisposeAsync([transaction], _completed, primary ?? _failure, logger ?? NullLogger.Instance);
    }
}
