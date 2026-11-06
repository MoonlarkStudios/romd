using ErrorOr;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Source.Rom;

namespace Romd.Infrastructure.ReferenceData;

/// <summary>Prepares a canonical reference outside the media lease, then stages it in caller custody.</summary>
/// <remarks>Use RedumpDiscReferenceImportComposition in production. The internal constructor exists
/// for friend tests; injected synthetic handlers never establish authenticated retrieval.
/// Call PrepareAsync before opening a database transaction. StageAsync requires an active caller-owned
/// transaction enforced by the port; the caller owns flush, commit and rollback on errors/cancellation.</remarks>
public sealed class RedumpDiscReferenceImporter : IDisposable
{
    private readonly RedumpDiscReferenceFetcher _fetcher;
    private readonly IRomDiscMediaReferenceImportStore _imports;
    private readonly HttpClient? _ownedClient;
    private int _disposed;

    internal RedumpDiscReferenceImporter(RedumpDiscReferenceFetcher fetcher,
        IRomDiscMediaReferenceImportStore imports, HttpClient? ownedClient = null)
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        ArgumentNullException.ThrowIfNull(imports);
        _fetcher = fetcher;
        _imports = imports;
        _ownedClient = ownedClient;
    }

    public async Task<ErrorOr<RedumpDiscReferenceImport>> PrepareAsync(int discId, Guid? recordId = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        if (recordId == Guid.Empty)
            return RomDiscMediaReferenceImportErrors.InvalidIdentity();
        Guid identity = recordId ?? Guid.NewGuid();
        var fetched = await _fetcher.FetchAsync(discId, ct);
        ct.ThrowIfCancellationRequested();
        if (fetched.IsError)
            return fetched.Errors;
        var retrieval = fetched.Value;
        byte[] rawBody = retrieval.CopyRawBody();
        var parsed = RedumpDiscMediaReferenceParser.Parse(rawBody, identity, retrieval.CanonicalSourceUrl,
            retrieval.DiscId, retrieval.RetrievedAtUtc, retrieval.AcceptedSourceProvenance, ct);
        ct.ThrowIfCancellationRequested();
        if (parsed.IsError)
            return parsed.Errors;
        return new RedumpDiscReferenceImport(parsed.Value, rawBody);
    }

    public async Task<ErrorOr<RomDiscMediaReferenceRecord>> StageAsync(RedumpDiscReferenceImport prepared,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(prepared);
        var staged = await _imports.StageAsync(prepared.Record, prepared.CopyRawBody(), ct);
        ct.ThrowIfCancellationRequested();
        if (staged.IsError)
            return staged.Errors;
        if (staged.Value != prepared.Record)
            return RomDiscMediaReferenceImportErrors.InvalidStagedResult();
        return staged;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _ownedClient?.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
