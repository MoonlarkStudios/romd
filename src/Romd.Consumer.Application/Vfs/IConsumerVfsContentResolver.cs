namespace Romd.Consumer.Application.Vfs;

/// <summary>Opens a seekable stream for a file that <see cref="ConsumerVfsService"/> authorized in this request.</summary>
public interface IConsumerVfsContentResolver
{
    Task<Stream?> OpenAsync(ConsumerVfsFile file, CancellationToken ct = default);
}
