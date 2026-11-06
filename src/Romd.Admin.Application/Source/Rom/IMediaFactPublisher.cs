using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom;

/// <summary>Worker publication inside the caller's fenced ReadCommitted transaction. Never commits independently.</summary>
/// <remarks>Database concurrency conflicts require rollback and replay of the entire caller unit of work; no statement-level retry is performed here.</remarks>
public interface IMediaFactPublisher
{
    Task<int> CreateLogicalAsync(string originalFilename, RomLogicalIdentity identity, Guid createdBy,
        DateTimeOffset createdAt, CancellationToken ct = default);
    Task<RomRepresentationFacts> PublishAsync(int artifactFileId, RomContainerFacts container, RomReconstructionManifest manifest,
        RomRepresentationVerification verification, IReadOnlyList<MediaOutputOwner> owners,
        IReadOnlyList<RomRetainedDescriptor> descriptors, CancellationToken ct = default);
    /// <returns>False when that decoder build already has its retained measurement.</returns>
    Task<bool> AppendVerificationAsync(int representationId, RomRepresentationVerification verification, CancellationToken ct = default);
}

/// <summary>Catalog association is independent of the immutable output bytes. Only owned outputs may become preferred.</summary>
public sealed record MediaOutputOwner(RomOutputSelector Selector, int? RomId, bool Preferred);
