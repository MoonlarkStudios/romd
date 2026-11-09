using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Media.Cd;

namespace Romd.Infrastructure.Storage;

public sealed partial class LocalTempFileFactory
{
    internal async Task<RetainedCdStaging> ReserveRetainedCdAsync(Guid jobId, IReadOnlyList<long> sourceBytes, CancellationToken ct)
    {
        if (jobId == Guid.Empty || sourceBytes.Count is < 2 or > 18 || sourceBytes.Any(bytes => bytes <= 0 || bytes > _maximumFile))
            throw new InvalidDataException("Retained CD staging exceeds its input bounds.");
        var lengths = sourceBytes.Append((long)ChdCdRequest.MaximumBytes).ToArray();
        var leases = await _pool.AcquireAggregateAsync(lengths, ct, 128);
        return new(this, jobId, lengths, leases);
    }
    internal sealed class RetainedCdStaging : IMediaStagingAggregate, IAsyncDisposable
    {
        private readonly LocalTempFileFactory _owner;
        private readonly TempFileCustody[] _custodies;
        private readonly long[] _lengths;
        private readonly int[] _used;
        internal RetainedCdStaging(LocalTempFileFactory owner, Guid jobId, long[] lengths, IReadOnlyList<TempFilePool.Lease> leases)
        {
            _owner = owner;
            _lengths = lengths;
            _used = new int[lengths.Length];
            _custodies = leases.Select(lease => new TempFileCustody(lease, owner._fileSystem, owner._logger, jobId, "media-verification-cd", "source")).ToArray();
        }
        public IReadOnlyList<MediaStagingBinding> StagingBindings => _custodies.Select(c => c.StagingBinding).ToArray();
        internal Task<ITempFile> MaterializeAsync(int ordinal, Stream input, Sha256? sha256, CancellationToken ct)
        {
            if (ordinal < 0 || ordinal >= _custodies.Length || Interlocked.Exchange(ref _used[ordinal], 1) != 0)
                throw new InvalidOperationException("A retained CD input can be staged only once.");
            return _owner.CopyCdInputAsync(_custodies[ordinal], input, _lengths[ordinal], sha256, ct);
        }
        public async ValueTask DisposeAsync()
        {
            List<Exception> errors = [];
            foreach (var custody in _custodies)
                try
                { await custody.DisposeAsync(); }
                catch (Exception error) { errors.Add(error); }
            if (errors.Count > 0)
                throw new AggregateException("Retained CD staging cleanup failed; custody remains retained.", errors);
        }
    }
}
