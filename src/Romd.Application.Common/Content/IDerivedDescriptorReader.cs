using Romd.Domain.Source.Rom;

namespace Romd.Application.Common.Content;

/// <summary>Exact current derived bytes after release authorization, in the caller's consistent database snapshot.
/// An available DAT-exact original takes priority. Returned bindings are not authorization capabilities.</summary>
public interface IDerivedDescriptorReader
{
    Task<IReadOnlyList<DerivedDescriptorBinding>> ReadForReleaseAsync(int releaseId, CancellationToken ct);
}

public sealed record DerivedDescriptorBinding(int Id, Guid Revision, int ReleaseId, int CatalogFileId, string RelativePath,
    int RepresentationId, int MemberOrdinal, int FileId, RomLogicalIdentity Identity, string GeneratorProfile);
