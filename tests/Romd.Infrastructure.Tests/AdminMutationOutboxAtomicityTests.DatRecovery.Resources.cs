using Romd.Admin.Application.Hashing;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;

namespace Romd.Infrastructure.Tests;

public sealed partial class AdminMutationOutboxAtomicityTests
{
    private sealed class DatRecoveryResources(string scope)
    {
        private readonly Dictionary<string, IOException> _failures = [];
        public List<string> Disposals { get; } = [];
        public List<string> TempPaths { get; } = [];
        public IOException FailureFor(string resource)
        {
            if (!_failures.TryGetValue(resource, out var failure))
                _failures.Add(resource, failure = new IOException($"Injected {resource} disposal failure."));
            return failure;
        }
        public ITempFile Wrap(ITempFile file)
        {
            TempPaths.Add(file.Path);
            return new Temporary(this, file);
        }
        public IStagedFile Wrap(IStagedFile file) => new Staged(this, file);
        public static IStagedFile Unwrap(IStagedFile file) => ((Staged)file).Inner;
        private void Disposed(string resource)
        {
            Disposals.Add(resource);
            if (scope == resource || scope == "all" && (resource is "staged" or "temp"))
                throw FailureFor(resource);
        }
        private sealed class Temporary(DatRecoveryResources owner, ITempFile inner) : ITempFile
        {
            private int _reads;
            public string Path => inner.Path;
            public DatHashResult? DatHashes => inner.DatHashes;
            public Sha256 FileSha256 => inner.FileSha256;
            public long FileSize => inner.FileSize;
            public bool TryBeginChildUse() => inner.TryBeginChildUse();
            public bool TryEndChildUse() => inner.TryEndChildUse();
            public bool Quarantine() => inner.Quarantine();
            public Stream OpenRead() => new ReadStream(owner, inner.OpenRead(), ++_reads switch
            {
                1 => "header",
                2 => "publication source",
                3 => "game",
                _ => "additional stream"
            });
            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                owner.Disposed("temp");
            }
        }
        private sealed class Staged(DatRecoveryResources owner, IStagedFile inner) : IStagedFile
        {
            public IStagedFile Inner { get; } = inner;
            public Sha256 Sha256 => Inner.Sha256;
            public long Size => Inner.Size;
            public Md5? ArtifactMd5 => Inner.ArtifactMd5;
            public Sha1? ArtifactSha1 => Inner.ArtifactSha1;
            public async ValueTask DisposeAsync()
            {
                await Inner.DisposeAsync();
                owner.Disposed("staged");
            }
        }
        private sealed class ReadStream(DatRecoveryResources owner, Stream inner, string name) : Stream
        {
            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => inner.CanSeek;
            public override bool CanWrite => inner.CanWrite;
            public override long Length => inner.Length;
            public override long Position { get => inner.Position; set => inner.Position = value; }
            public override void Flush() => inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                inner.ReadAsync(buffer, offset, count, cancellationToken);
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                inner.ReadAsync(buffer, cancellationToken);
            public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
            public override void SetLength(long value) => inner.SetLength(value);
            public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
            public override async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                GC.SuppressFinalize(this);
                owner.Disposed(name);
            }
        }
    }
}
