using System.Buffers;
using ErrorOr;
using Romd.Admin.Application.Hashing;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Infrastructure.Hashing;
using Romd.Media.Cd;

namespace Romd.Infrastructure.Storage;

public sealed partial class LocalTempFileFactory
{
    /// <summary>Reserve every original, request and output in one main-pool transaction, plus one bounded .cue.
    /// The CUE is acquired first consistently and released if main admission fails; no partial attempt escapes.</summary>
    internal async Task<CdAttemptStaging> ReserveCdAttemptAsync(Guid jobId, IReadOnlyList<long> sourceBytes,
        long maximumOutputBytes, CancellationToken ct)
    {
        if (jobId == Guid.Empty || sourceBytes.Count is < 2 or > 116 || sourceBytes.Any(bytes => bytes <= 0) ||
            maximumOutputBytes is < 124 or > ChdCdLimits.MaximumArtifactBytes)
            throw new InvalidDataException("CD attempt is outside its bounded shape.");
        // Configured staging limits are an operator setting, not a source or output mismatch.
        if (sourceBytes.Count + 2 > _limits.MaximumFiles || sourceBytes.Any(bytes => bytes > _maximumFile) || maximumOutputBytes > _maximumFile)
            throw new RomCdStagingLimitException();
        var cue = await _cuePool.Value.AcquireAsync(ChdCdRequest.MaximumBytes, ct);
        try
        {
            var leases = await _pool.AcquireAggregateAsync([.. sourceBytes, ChdCdRequest.MaximumBytes, maximumOutputBytes], ct, 128);
            return new(this, jobId, sourceBytes.ToArray(), leases, cue, maximumOutputBytes, "encode-chd-cd");
        }
        catch { await cue.DisposeAsync(); throw; }
    }

    /// <summary>Reserve the retained artifact, every original and the request together, with no encoded output.
    /// The separate CUE pool follows the same acquisition order as encoding.</summary>
    internal async Task<CdAttemptStaging> ReserveCdComparisonAsync(Guid jobId, long artifactBytes,
        IReadOnlyList<long> originalBytes, CancellationToken ct)
    {
        if (jobId == Guid.Empty || artifactBytes is < 124 or > ChdCdLimits.MaximumArtifactBytes ||
            originalBytes.Count is < 2 or > 116 || originalBytes.Any(bytes => bytes <= 0))
            throw new InvalidDataException("CD comparison is outside its bounded shape.");
        long[] sourceBytes = [artifactBytes, .. originalBytes];
        if (sourceBytes.Length + 1 > _limits.MaximumFiles || sourceBytes.Any(bytes => bytes > _maximumFile))
            throw new RomCdStagingLimitException();
        var cue = await _cuePool.Value.AcquireAsync(ChdCdRequest.MaximumBytes, ct);
        try
        {
            var leases = await _pool.AcquireAggregateAsync([.. sourceBytes, ChdCdRequest.MaximumBytes], ct, 128);
            return new(this, jobId, sourceBytes, leases, cue, null, "verify-chd-cd");
        }
        catch { await cue.DisposeAsync(); throw; }
    }

    /// <summary>Single-attempt factory: it can create only its one precharged request, tool CUE and output.
    /// Original materialization is explicit by ordinal. All bindings are durable before any source copy.</summary>
    internal sealed class CdAttemptStaging : ITempFileFactory, IMediaStagingAggregate, IAsyncDisposable
    {
        private readonly LocalTempFileFactory _owner;
        private readonly TempFileCustody[] _sources;
        private readonly TempFileCustody _request, _cue;
        private readonly TempFileCustody? _output;
        private readonly long[] _sourceBytes;
        private readonly int[] _sourceStates;
        private readonly long? _maximumOutput;
        private int _requestState, _cueState, _outputState, _disposed;
        internal CdAttemptStaging(LocalTempFileFactory owner, Guid jobId, long[] sourceBytes,
            IReadOnlyList<TempFilePool.Lease> leases, TempFilePool.Lease cue, long? maximumOutput, string operation)
        {
            _owner = owner;
            _sourceBytes = sourceBytes;
            _maximumOutput = maximumOutput;
            _sourceStates = new int[sourceBytes.Length];
            _sources = leases.Take(sourceBytes.Length).Select(lease => Custody(lease, "source")).ToArray();
            _request = Custody(leases[sourceBytes.Length], "source");
            _output = maximumOutput is not null ? Custody(leases[sourceBytes.Length + 1], "output") : null;
            _cue = Custody(cue, "source");
            TempFileCustody Custody(TempFilePool.Lease lease, string role) => new(lease, owner._fileSystem, owner._logger, jobId, operation, role);
        }
        private IEnumerable<TempFileCustody> Custodies => _output is { } output
            ? _sources.Append(_request).Append(_cue).Append(output) : _sources.Append(_request).Append(_cue);
        public IReadOnlyList<MediaStagingBinding> StagingBindings => Custodies.Select(custody => custody.StagingBinding).ToArray();

        internal Task<ITempFile> MaterializeAsync(int ordinal, Stream input, Sha256 expectedSha256, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (ordinal < 0 || ordinal >= _sources.Length || expectedSha256.IsEmpty || Interlocked.CompareExchange(ref _sourceStates[ordinal], 1, 0) != 0)
                throw new InvalidDataException("A CD source can be materialized only once under its exact frozen identity.");
            return _owner.CopyCdInputAsync(_sources[ordinal], input, _sourceBytes[ordinal], expectedSha256, ct);
        }

        public Task<ITempFile> CreateAsync(Stream source, CancellationToken cancellationToken = default,
            Func<ReadOnlyMemory<byte>, bool>? includeDatHashes = null,
            Func<ReadOnlyMemory<byte>, CancellationToken, Task>? beforeCopy = null, Func<ReadOnlyMemory<byte>, int>? selectPrefixLength = null)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (includeDatHashes is not null || beforeCopy is not null || selectPrefixLength is not null || Interlocked.CompareExchange(ref _requestState, 1, 0) != 0)
                throw new InvalidOperationException("CD attempt factory creates exactly one bounded generated request.");
            return _owner.CopyCdInputAsync(_request, source, ChdCdRequest.MaximumBytes, null, cancellationToken);
        }
        public Task<ErrorOr<IWritableTempFile>> ReserveCdCueAsync(long maximumBytes, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (maximumBytes != ChdCdRequest.MaximumBytes || Interlocked.CompareExchange(ref _cueState, 1, 0) != 0)
                throw new InvalidOperationException("CD attempt owns exactly one precharged tool CUE.");
            return Task.FromResult<ErrorOr<IWritableTempFile>>(new LocalWritableTempFile(_cue, maximumBytes));
        }
        public Task<ErrorOr<IWritableTempFile>> ReserveOutputAsync(long maximumBytes, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_output is null || maximumBytes != _maximumOutput || Interlocked.CompareExchange(ref _outputState, 1, 0) != 0)
                throw new InvalidOperationException("CD attempt owns exactly one precharged encoded output.");
            return Task.FromResult<ErrorOr<IWritableTempFile>>(new LocalWritableTempFile(_output, maximumBytes));
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            List<Exception> errors = [];
            foreach (var custody in Custodies)
                try
                { await custody.DisposeAsync(); }
                catch (Exception error) { errors.Add(error); }
            if (errors.Count > 0)
                throw new AggregateException("CD attempt staging cleanup failed; unresolved charges remain retained.", errors);
        }
    }

    private async Task<ITempFile> CopyCdInputAsync(TempFileCustody custody, Stream source, long maximum, Sha256? expected, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(_limits.CopyTimeoutSeconds));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(131072);
        try
        {
            long written = 0;
            using var hashed = new MultiHashReadStream(source, HashAlgorithms.All, leaveOpen: true);
            await using (var output = _fileSystem.File.Open(custody.Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                int count;
                while ((count = await hashed.ReadAsync(buffer.AsMemory(0, 131072), deadline.Token)) != 0)
                {
                    if (count > maximum - written)
                        throw new InvalidDataException("CD source exceeds its reserved byte ceiling.");
                    await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
                    written += count;
                }
                await output.FlushAsync(deadline.Token);
            }
            var hashes = hashed.GetComputedHashes();
            if (written <= 0 || expected is { } identity && (written != maximum || hashes.Sha256 != identity))
                throw new InvalidDataException("CD source differs from the durable SHA256 and length.");
            return new LocalTempFile(custody, hashes.Sha256, written, new DatHashResult(hashes.Sha1, hashes.Md5, hashes.Crc32, written));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new InvalidDataException("CD source materialization exceeded its admitted deadline."); }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
