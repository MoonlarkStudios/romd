using Romd.Application.Common.Content;
using Romd.Domain.Paths;

namespace Romd.Consumer.Application.Delivery;

/// <summary>Checks the exact captured, retained descriptor after a signed delivery capability has been validated.
/// New-manifest original priority is separate: an original arrival never rewrites captured derived bytes.</summary>
public interface IConsumerDerivedDescriptorSource
{
    Task<bool> IsRetainedAsync(DerivedDescriptorBinding captured, CancellationToken ct = default);
}

public static class ConsumerDerivedDescriptors
{
    public const long MaximumBytes = 16L << 20;
    public static bool IsValid(DerivedDescriptorBinding source) => source is
    {
        Id: > 0, Revision: var revision, ReleaseId: > 0, CatalogFileId: > 0, RepresentationId: > 0,
        MemberOrdinal: >= 0 and <= 198, FileId: > 0, Identity: { } identity
    }
        && revision != Guid.Empty && identity.Size is > 0 and <= MaximumBytes && !identity.Sha256.IsEmpty && !identity.Sha1.IsEmpty && !identity.Md5.IsEmpty
        && PortableRelativePath.IsSafe(source.RelativePath) && source.RelativePath.Length <= 240
        && source.RelativePath.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(source.GeneratorProfile) && source.GeneratorProfile.Length <= 128 && !source.GeneratorProfile.Any(char.IsControl);
}
