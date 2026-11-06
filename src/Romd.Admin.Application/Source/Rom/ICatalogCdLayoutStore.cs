using ErrorOr;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom;

/// <summary>A captured DAT requirement; its identity is never replaced with the generated file's identity.</summary>
public sealed record CatalogDescriptorRequirement(int CatalogFileId, int ReleaseId, string ReleaseFingerprint, string FileFingerprint,
    string Path, long Size, Sha1 Sha1, Md5 Md5, Crc32 Crc32)
{
    public bool Matches(RomLogicalIdentity actual) => actual.Size == Size && actual.Sha1 == Sha1 && actual.Md5 == Md5 && actual.Crc32 == Crc32;
}

/// <summary>Measured generated-CUE association. Expected and actual identities determine descriptor provenance; original availability remains a logical-ROM fact.</summary>
public sealed record CatalogCdLayoutPublication(Guid Revision, CatalogDescriptorRequirement Requirement,
    int RepresentationId, int MemberOrdinal, string GeneratorProfile, DateTimeOffset CreatedAt);

/// <summary>Worker-only publication inside the caller's ReadCommitted transaction; never commits or stages CAS itself.</summary>
public interface ICatalogCdLayoutStore
{
    /// <summary>Acquire before logical ROM, representation and file locks. False means the captured catalog changed.</summary>
    Task<bool> LockScopeAsync(CatalogDescriptorRequirement requirement, CancellationToken ct);
    Task<ErrorOr<int>> PublishAsync(CatalogCdLayoutPublication publication, CancellationToken ct);
    Task<bool> WithdrawAsync(int id, Guid revision, CancellationToken ct);
}
