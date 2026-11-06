using Microsoft.Extensions.DependencyInjection;
using Romd.Storage;

// Child-process fixture for storage crash/lease tests. Never points at a live CAS root.
namespace Romd.Storage.TestHost;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var services = new ServiceCollection();
        services.AddContentAddressableStorage(options => options.RootPath = args[0]);
        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IContentAddressableStore>();
        // A non-seekable source holds a live staging file outside the mutation lease, then waits to be killed.
        await store.StoreAsync(new PausedSource());
    }

    private sealed class PausedSource : Stream
    {
        private bool _started;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_started)
            {
                _started = true;
                buffer.Span.Fill(42);
                return buffer.Length;
            }
            Console.WriteLine("staged");
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
