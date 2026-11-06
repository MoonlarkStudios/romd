using Romd.Domain.Source.Rom;

namespace Romd.Application.Common.Content;

/// <summary>Reads retained facts. The result establishes neither request authorization nor host admission.</summary>
public interface IMediaFactReader
{
    const int MaximumBatchSize = 256;
    /// <summary>Uses its own read snapshot, or a caller-owned RepeatableRead/Serializable transaction. Refuses an ambient ReadCommitted transaction.</summary>
    Task<IReadOnlyDictionary<int, RomRepresentationFacts>> ReadAsync(IReadOnlyCollection<int> representationIds,
        CancellationToken ct = default);
}
