using Romd.Application.Common.Content;
using Romd.Consumer.Application.Libraries;

namespace Romd.Consumer.Application.Delivery;

public interface IConsumerCdArchiveRepository
{
    Task<ConsumerLibraryReadResult<RomCdArchiveDownload>> FindAsync(ConsumerLibraryScope scope, int releaseId, CancellationToken ct = default);
}
