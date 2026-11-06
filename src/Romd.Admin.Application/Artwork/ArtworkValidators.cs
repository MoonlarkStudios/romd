using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Artwork;

/// <summary>
///     The validators of a title's artwork (docs/decisions/api-contract-policy.md, "Idempotency And Concurrency").
///     Each role is versioned by its own revision. The family, which guards the title's saved assets, rejections and
///     hidden candidates, is versioned by a digest of its own mutable state: every role revision in role order, then
///     the sorted saved-asset ids, rejected-asset ids and hidden-candidate ids. Every family write changes one of
///     them, so it changes the family validator. A role without a selection counts as revision 0, as
///     <see cref="IArtworkCurationRepository.GetStatesAsync" /> reads it.
/// </summary>
public static class ArtworkValidators
{
    public static string Role(long revision) => revision.ToString(CultureInfo.InvariantCulture);

    public static string Family(IEnumerable<(ArtworkRole Role, long Revision)> revisions, IEnumerable<int> savedAssetIds,
        IEnumerable<int> rejectedAssetIds, IEnumerable<string> hiddenCandidateIds)
    {
        var byRole = revisions.ToDictionary(item => item.Role, item => item.Revision);
        var canonical = string.Join('|',
            string.Join(';', Enum.GetValues<ArtworkRole>().Select(role => $"{role}:{Role(byRole.GetValueOrDefault(role))}")),
            Ids(savedAssetIds),
            Ids(rejectedAssetIds),
            string.Join(';', hiddenCandidateIds.Order(StringComparer.Ordinal)));
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))[..16]).ToString();
    }

    private static string Ids(IEnumerable<int> ids) =>
        string.Join(';', ids.Order().Select(id => id.ToString(CultureInfo.InvariantCulture)));
}

/// <summary>
///     The stable identity of a provider artwork candidate: the first 16 bytes of the SHA-256 of its source URL, as 32
///     lowercase hex digits. Hiding stores the source URL, so a hidden candidate keeps this identity without a second
///     key, and the signed candidate reference never has to appear in a URL.
/// </summary>
public static class ArtworkCandidateIds
{
    public static string Of(string sourceUrl) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sourceUrl))[..16]);
}

/// <summary>A title's artwork roles and the family validator computed from the same reads.</summary>
public sealed record ArtworkFamily(IReadOnlyList<ArtworkCurationState> Roles, string Revision);
