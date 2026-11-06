using ErrorOr;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom.Compressed;

/// <summary>Worker authority inside the caller's ReadCommitted transaction. Acquires topology/catalog, logical, graph and file locks in order.</summary>
public interface IRomCdImportStore
{
    Task<ErrorOr<IRomCdImportScope>> LockAsync(RomCdImportPlan plan, IReadOnlyList<string> platformKeys, Guid actorId, DateTimeOffset now, CancellationToken ct);
}

/// <summary>One exact transaction's owned identities and retained facts; completion never commits and cannot be replayed in another transaction.</summary>
public interface IRomCdImportScope
{
    IReadOnlyDictionary<string, int> BinRomIds { get; }
    RomRepresentationFacts? CurrentArchive { get; }
    IReadOnlyList<RomRepresentationFacts> RetainedArchives { get; }
    bool HasNewLogicalOwners { get; }
    Task<ErrorOr<RomCdImportRetained>> CompleteAsync(RomRepresentationFacts target, CancellationToken ct);
}

public sealed record RomCdImportRetained(RomRepresentationFacts Archive, RomRepresentationFacts? OriginalCue, int LinkedRequirements);
