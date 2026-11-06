using System.Buffers;
using System.IO.Abstractions;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Hashing;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Infrastructure.Hashing;

namespace Romd.Infrastructure.Storage;

public sealed partial class LocalTempFileFactory : ITempFileFactory, IAggregateTempFileFactory
{
    private readonly IFileSystem _fileSystem;
    private readonly TempFilePool _pool;
    private readonly Lazy<TempFilePool> _cuePool;
    private readonly TempStorageOptions _limits;
    private readonly ILogger<LocalTempFileFactory> _logger;
    private readonly long _maximumFile;

    public LocalTempFileFactory(IRomdOptions options, IFileSystem fileSystem,
        TempStorageOptions? limits = null, ILogger<LocalTempFileFactory>? logger = null)
    {
        _fileSystem = fileSystem;
        long maximumFile = options.MaxUploadBytes > 0 ? options.MaxUploadBytes : 64L << 30;
        _maximumFile = maximumFile;
        _limits = (limits ?? new()).ValidatedCopy(maximumFile);
        _logger = logger ?? NullLogger<LocalTempFileFactory>.Instance;
        _pool = new(fileSystem, Path.GetFullPath(Path.Combine(options.DataDirectory, "temp", "staging-v1")), _limits, maximumFile);
        _cuePool = new(() =>
        {
            long maximumCue = Math.Min(maximumFile, 512 << 10);
            var cueLimits = _limits.ValidatedCopy(maximumFile);
            cueLimits.MaximumBytes = Math.Min(cueLimits.MaximumBytes, maximumCue * cueLimits.MaximumFiles);
            return new(fileSystem, Path.GetFullPath(Path.Combine(options.DataDirectory, "temp", TempFilePool.CdCuePoolName)), cueLimits, maximumCue);
        });
    }

    public async Task<ErrorOr<IWritableTempFile>> ReserveOutputAsync(long maximumBytes, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (maximumBytes <= 0 || maximumBytes > _maximumFile)
            return Error.Validation("Temp.OutputLimit", "The encoded output bound exceeds the staging file limit.");
        var lease = await _pool.AcquireAsync(maximumBytes, ct);
        return new LocalWritableTempFile(new TempFileCustody(lease, _fileSystem, _logger), maximumBytes);
    }

    public async Task<ErrorOr<IWritableTempFile>> ReserveCdCueAsync(long maximumBytes, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (maximumBytes <= 0 || maximumBytes > Math.Min(_maximumFile, 512 << 10))
            return Error.Validation("Temp.CdCueLimit", "The generated CD descriptor exceeds its staging limit.");
        var lease = await _cuePool.Value.AcquireAsync(maximumBytes, ct);
        return new LocalWritableTempFile(new TempFileCustody(lease, _fileSystem, _logger), maximumBytes);
    }

    public async Task<ErrorOr<ITempFileAggregate>> ReserveAggregateAsync(TempFileAggregateRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (request.JobId == Guid.Empty || string.IsNullOrWhiteSpace(request.Operation) || request.Operation.Length > 64 ||
            request.Operation.Any(character => !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')) ||
            request.SourceBytes <= 0 || request.SourceBytes > _maximumFile || request.OutputMaximumBytes < 0 ||
            request.OutputMaximumBytes > _maximumFile || request.VerificationMaximumBytes < 0 || request.VerificationMaximumBytes > _maximumFile)
            return Error.Validation("Temp.AggregateLimit", "The aggregate staging identity or byte ceilings are invalid.");
        var lengths = new List<long> { request.SourceBytes };
        if (request.OutputMaximumBytes > 0)
            lengths.Add(request.OutputMaximumBytes);
        if (request.VerificationMaximumBytes > 0)
            lengths.Add(request.VerificationMaximumBytes);
        var leases = await _pool.AcquireAggregateAsync(lengths, ct);
        return new LocalTempFileAggregate(request, leases, _fileSystem, _logger, _limits.CopyTimeoutSeconds);
    }

    public async Task<ITempFile> CreateAsync(Stream source, CancellationToken cancellationToken = default,
        Func<ReadOnlyMemory<byte>, bool>? includeDatHashes = null,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task>? beforeCopy = null,
        Func<ReadOnlyMemory<byte>, int>? selectPrefixLength = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        long? knownLength = source.CanSeek ? checked(source.Length - source.Position) : null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        TempFilePool.Lease? lease = null;
        byte[]? buffer = null;
        deadline.CancelAfter(TimeSpan.FromSeconds(_limits.CopyTimeoutSeconds));
        try
        {
            long fileSize = 0;
            byte[] prefix = includeDatHashes is null && beforeCopy is null && selectPrefixLength is null ? [] : new byte[4];
            int prefixLength = prefix.Length == 0 ? 0 : await source.ReadAtLeastAsync(prefix, prefix.Length,
                throwOnEndOfStream: false, deadline.Token);
            if (selectPrefixLength is not null)
            {
                for (int round = 0; round < 2; round++)
                {
                    int selected = selectPrefixLength(prefix.AsMemory(0, prefixLength));
                    if (selected is < 4 or > RomPreparationDetection.MaximumPrefixBytes)
                        throw new ArgumentOutOfRangeException(nameof(selectPrefixLength), "Detection prefix exceeds the bounded ISO9660 header.");
                    if (prefixLength != prefix.Length || selected <= prefix.Length)
                        break;
                    Array.Resize(ref prefix, selected);
                    prefixLength += await source.ReadAtLeastAsync(prefix.AsMemory(prefixLength), selected - prefixLength,
                        throwOnEndOfStream: false, deadline.Token);
                }
            }
            deadline.Token.ThrowIfCancellationRequested();
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            bool datHashes = includeDatHashes?.Invoke(prefix.AsMemory(0, prefixLength)) == true;
            if (beforeCopy is not null)
                await beforeCopy(prefix.AsMemory(0, prefixLength), cancellationToken);
            lease = await _pool.AcquireAsync(knownLength, cancellationToken);
            buffer = ArrayPool<byte>.Shared.Rent(131072);
            // Admission has its own bounds; give the admitted copy its full file deadline.
            deadline.CancelAfter(TimeSpan.FromSeconds(_limits.CopyTimeoutSeconds));
            using var replay = new PrefixReadStream(source, prefix.AsMemory(0, prefixLength));
            using var hashed = new MultiHashReadStream(replay, datHashes ? HashAlgorithms.All : HashAlgorithms.Sha256, leaveOpen: true);
            await using (var file = _fileSystem.File.Open(lease.Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                int count;
                while ((count = await hashed.ReadAsync(buffer.AsMemory(0, 131072), deadline.Token)) != 0)
                {
                    long next = checked(fileSize + count);
                    await lease.EnsureCapacityAsync(next, deadline.Token);
                    await file.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
                    fileSize = next;
                }
                await file.FlushAsync(deadline.Token);
            }
            deadline.Token.ThrowIfCancellationRequested();
            var hashes = hashed.GetComputedHashes();
            return new LocalTempFile(new TempFileCustody(lease, _fileSystem, _logger), hashes.Sha256, fileSize,
                datHashes ? new(hashes.Sha1, hashes.Md5, hashes.Crc32, fileSize) : null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            if (lease is not null)
                await CleanupAsync(lease, _logger);
            throw new InvalidDataException("Temporary staging copy exceeded this file's deadline.");
        }
        catch
        {
            if (lease is not null)
                await CleanupAsync(lease, _logger);
            throw;
        }
        finally { if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static async ValueTask CleanupAsync(TempFilePool.Lease lease, ILogger logger)
    {
        try
        { await lease.DisposeAsync(); }
        catch (Exception error)
        {
            // Failed cleanup retains accounting. Another guarded admission can reclaim
            // the now-unowned slot; never silently pretend its bytes were released.
            logger.LogWarning(error, "Temporary staging cleanup failed for {Path}; its reservation is retained", lease.Path);
        }
    }

    private sealed class LocalTempFile(TempFileCustody custody, Sha256 hash, long size, DatHashResult? datHashes) : ITempFile, IMediaStagingBinding
    {
        public DatHashResult? DatHashes { get; } = datHashes;
        public string Path => custody.Path;
        public Sha256 FileSha256 { get; } = hash;
        public long FileSize { get; } = size;
        public MediaStagingBinding StagingBinding => custody.StagingBinding;
        public Stream OpenRead() => custody.OpenRead();
        public bool TryBeginChildUse() => custody.TryBeginChildUse();
        public bool TryEndChildUse() => custody.TryEndChildUse();
        public bool Quarantine() => custody.Quarantine();
        public ValueTask DisposeAsync() => custody.DisposeAsync();
    }

    private sealed class LocalWritableTempFile(TempFileCustody custody, long maximumBytes) : IWritableTempFile, IMediaStagingBinding
    {
        private int _state; // Writable, transferred, disposed.
        public string Path => custody.Path;
        public long MaximumBytes { get; } = maximumBytes;
        public MediaStagingBinding StagingBinding => custody.StagingBinding;
        public bool TryBeginChildUse() { lock (custody.Gate) return _state == 0 && custody.TryBeginChildUse(); }
        public bool TryEndChildUse() => custody.TryEndChildUse();
        public bool Quarantine() => custody.Quarantine();
        public async Task<ErrorOr<ITempFile>> SealAsync(TempFileArtifactMeasurement measurement, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (custody.Gate)
            {
                if (_state != 0 || !custody.CanSeal || measurement.Size <= 0 || measurement.Size > MaximumBytes ||
                    measurement.Sha256.IsEmpty || measurement.Md5.IsEmpty || measurement.Sha1.IsEmpty)
                    return Error.Validation("Temp.OutputFacts", "The output cannot be sealed with these artifact facts or custody state.");
                _state = 3; // No new child use or transfer while accounting is finalized.
            }
            try
            {
                if (!await custody.ReduceToMeasuredAsync(measurement.Size, ct))
                    return Error.Validation("Temp.OutputFacts", "The output length or custody does not match its complete measurement.");
                lock (custody.Gate)
                {
                    if (_state != 3 || !custody.CanSeal)
                        return Error.Validation("Temp.OutputFacts", "Output custody changed while sealing.");
                    _state = 1;
                    return new LocalTempFile(custody, measurement.Sha256, measurement.Size, null);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ResourceCapacityException)
            {
                return Error.Validation("Temp.OutputIo", "The encoded artifact's reservation could not be finalized.");
            }
            finally
            {
                lock (custody.Gate)
                { if (_state == 3) _state = 0; }
            }
        }
        public ValueTask DisposeAsync()
        {
            lock (custody.Gate)
            {
                if (_state is 1 or 2)
                    return ValueTask.CompletedTask;
                if (_state == 3)
                    custody.Quarantine();
                _state = 2;
                return custody.DisposeAsync();
            }
        }
    }

    private sealed class TempFileCustody(TempFilePool.Lease lease, IFileSystem fs, ILogger logger,
        Guid? jobId = null, string? attemptOperation = null, string role = "source")
    {
        private readonly TempFilePool.Lease _lease = lease;
        private readonly ILogger _logger = logger;
        private int _disposed;
        internal object Gate { get; } = new();
        private int _childUses;
        private bool _quarantined;
        internal IFileSystem FileSystem { get; } = fs;
        internal string Path => _lease.Path;
        internal MediaStagingBinding StagingBinding => new(_lease.PoolRoot, _lease.Slot, Path, _lease.Reserved,
            jobId, attemptOperation, role);
        internal bool CanSeal => _disposed == 0 && _childUses == 0 && !_quarantined && !FileSystem.File.Exists(Path + ".quarantine");
        internal Task<bool> ReduceToMeasuredAsync(long size, CancellationToken ct) => _lease.ReduceToMeasuredAsync(size, ct);
        internal Stream OpenRead()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return FileSystem.File.OpenRead(Path);
        }
        public bool TryBeginChildUse()
        {
            lock (Gate)
            {
                if (_disposed != 0 || _quarantined)
                    return false;
                if (!EnsureMarker())
                    return false;
                _childUses++;
                return true;
            }
        }
        public bool TryEndChildUse()
        {
            lock (Gate)
            {
                if (_childUses == 0 || _quarantined)
                    return false;
                if (--_childUses != 0)
                    return true;
                try
                { FileSystem.File.Delete(Path + ".quarantine"); return true; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Retain(error);
                    return false;
                }
            }
        }
        public bool Quarantine()
        {
            lock (Gate)
            {
                _quarantined = true;
                _lease.Retain(); // Keep accounting and the live OS lease even if persistence fails.
                return EnsureMarker();
            }
        }
        private bool EnsureMarker()
        {
            try
            {
                if (FileSystem.File.Exists(Path + ".quarantine"))
                    return true;
                using var marker = FileSystem.File.Open(Path + ".quarantine", FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                if (MediaProcessingRuntime.Current is { } run)
                {
                    byte[] contents = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
                    { Version = "romd-media-hold/v1", RunId = run.Nonce, RecordPath = run.RecordPath });
                    marker.Write(contents);
                }
                marker.Flush(flushToDisk: true);
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Temporary source custody persistence failed with {FailureType}", error.GetType().Name);
                return false;
            }
        }
        private void Retain(Exception error)
        {
            _quarantined = true;
            _lease.Retain();
            _logger.LogWarning("Temporary source custody release failed with {FailureType}; reservation retained", error.GetType().Name);
        }
        public ValueTask DisposeAsync()
        {
            lock (Gate)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return ValueTask.CompletedTask;
                if (_quarantined || _childUses != 0)
                {
                    _quarantined = true;
                    _lease.Retain();
                    return ValueTask.CompletedTask;
                }
                return CleanupAsync(_lease, _logger);
            }
        }
    }

    private sealed class LocalTempFileAggregate : ITempFileAggregate, IMediaStagingAggregate
    {
        private readonly TempFileAggregateRequest _request;
        private readonly TempFileCustody _source;
        private readonly IReadOnlyList<TempFileCustody> _custodies;
        private readonly IFileSystem _fs;
        private readonly int _copyTimeout;
        private int _state;
        public Guid JobId => _request.JobId;
        public IWritableTempFile? Output { get; }
        public IWritableTempFile? Verification { get; }
        public IReadOnlyList<MediaStagingBinding> StagingBindings => _custodies.Select(custody => custody.StagingBinding).ToArray();

        internal LocalTempFileAggregate(TempFileAggregateRequest request, IReadOnlyList<TempFilePool.Lease> leases,
            IFileSystem fs, ILogger logger, int copyTimeout)
        {
            _request = request;
            _fs = fs;
            _copyTimeout = copyTimeout;
            var custodies = new List<TempFileCustody>();
            _source = new(leases[0], fs, logger, request.JobId, request.Operation, "source");
            custodies.Add(_source);
            int index = 1;
            if (request.OutputMaximumBytes > 0)
            {
                var custody = new TempFileCustody(leases[index++], fs, logger, request.JobId, request.Operation, "output");
                custodies.Add(custody);
                Output = new LocalWritableTempFile(custody, request.OutputMaximumBytes);
            }
            if (request.VerificationMaximumBytes > 0)
            {
                var custody = new TempFileCustody(leases[index], fs, logger, request.JobId, request.Operation, "verification");
                custodies.Add(custody);
                Verification = new LocalWritableTempFile(custody, request.VerificationMaximumBytes);
            }
            _custodies = custodies;
        }

        public async Task<ErrorOr<ITempFile>> MaterializeSourceAsync(Stream input, Sha256 expectedSha256, long expectedBytes,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (expectedSha256.IsEmpty || expectedBytes != _request.SourceBytes || Interlocked.CompareExchange(ref _state, 1, 0) != 0)
                return Error.Validation("Temp.SourceIdentity", "The owned source identity or aggregate state is invalid.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(_copyTimeout));
            byte[] buffer = ArrayPool<byte>.Shared.Rent(131072);
            try
            {
                using var hashed = new MultiHashReadStream(input, HashAlgorithms.All, leaveOpen: true);
                long written = 0;
                await using (var destination = _fs.File.Open(_source.Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    int count;
                    while ((count = await hashed.ReadAsync(buffer.AsMemory(0, 131072), deadline.Token)) > 0)
                    {
                        if (count > expectedBytes - written)
                            return Error.Validation("Temp.SourceIdentity", "The materialized source exceeds its owned length.");
                        await destination.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
                        written += count;
                    }
                    await destination.FlushAsync(deadline.Token);
                }
                var facts = hashed.GetComputedHashes();
                if (written != expectedBytes || facts.Sha256 != expectedSha256)
                    return Error.Validation("Temp.SourceIdentity", "The materialized source differs from its owned SHA-256 or length.");
                return new LocalTempFile(_source, facts.Sha256, written, new(facts.Sha1, facts.Md5, facts.Crc32, written));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { return Error.Validation("Temp.SourceDeadline", "Source materialization exceeded its admitted copy deadline."); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { return Error.Validation("Temp.SourceIo", "The owned source could not be materialized."); }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        public async ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _state, 2);
            foreach (var custody in _custodies)
                await custody.DisposeAsync();
        }
    }

    // Replays only the bounded detection prefix, preserving non-seekable input and
    // source ownership while hashing and copying every byte exactly once.
    private sealed class PrefixReadStream(Stream source, ReadOnlyMemory<byte> prefix) : Stream
    {
        private ReadOnlyMemory<byte> _remaining = prefix;
        private int ReadPrefix(Span<byte> buffer)
        {
            int count = Math.Min(buffer.Length, _remaining.Length);
            _remaining.Span[..count].CopyTo(buffer);
            _remaining = _remaining[count..];
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => _remaining.IsEmpty
            ? source.Read(buffer, offset, count) : ReadPrefix(buffer.AsSpan(offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return _remaining.IsEmpty ? source.ReadAsync(buffer, ct) : ValueTask.FromResult(ReadPrefix(buffer.Span));
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
