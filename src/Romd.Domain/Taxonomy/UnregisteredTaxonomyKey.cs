using System.Security.Cryptography;
using System.Text;

namespace Romd.Domain.Taxonomy;

/// <summary>
///     The natural key minted once for an unregistered region or language row, from the matcher-normalized DAT
///     token that created it. Keys live in the reserved <c>dat-</c> namespace, which built-in keys never use, so
///     minting cannot collide with a registration. A key is stored when its row is created and never recomputed;
///     the schema migration that backfilled existing rows applies the same candidate order.
/// </summary>
public static class UnregisteredTaxonomyKey
{
    /// <summary>The reserved prefix of every unregistered key.</summary>
    public const string Prefix = "dat-";

    /// <summary>The longest slug a key carries, before any hash or ordinal suffix.</summary>
    public const int MaxSlugLength = 40;

    /// <summary>The highest ordinal suffix, which keeps every candidate within 64 characters.</summary>
    public const int MaxOrdinal = 999;

    private const int HashLength = 10;

    /// <summary>
    ///     The candidates for a matcher-normalized token, in preference order: <c>dat-{slug}</c> (skipped when the
    ///     slug is empty), then <c>dat-{slug}-{hash}</c> (<c>dat-{hash}</c> for an empty slug), then that stem with
    ///     <c>-2</c> to <c>-999</c>. The ordinal is reachable only when two rows derive from the same token.
    /// </summary>
    public static IEnumerable<string> Candidates(string normalizedToken)
    {
        ArgumentNullException.ThrowIfNull(normalizedToken);
        string slug = Slug(normalizedToken);
        string hashed = HashedStem(normalizedToken, slug);
        if (slug.Length > 0)
            yield return Prefix + slug;
        yield return hashed;
        for (int ordinal = 2; ordinal <= MaxOrdinal; ordinal++)
            yield return $"{hashed}-{ordinal}";
    }

    /// <summary>The stem every candidate after the slug-only one starts with.</summary>
    public static string HashedStem(string normalizedToken)
    {
        ArgumentNullException.ThrowIfNull(normalizedToken);
        return HashedStem(normalizedToken, Slug(normalizedToken));
    }

    /// <summary>The first candidate that <paramref name="isTaken" /> does not report as an existing key.</summary>
    public static string Mint(string normalizedToken, Func<string, bool> isTaken)
    {
        ArgumentNullException.ThrowIfNull(isTaken);
        return Candidates(normalizedToken).FirstOrDefault(candidate => !isTaken(candidate))
               ?? throw new InvalidOperationException(
                   $"Every unregistered key candidate up to ordinal {MaxOrdinal} is taken for one token.");
    }

    /// <summary>
    ///     Replaces each run of characters outside <c>[a-z0-9]</c> with <c>-</c>, trims <c>-</c> from both ends,
    ///     truncates to <see cref="MaxSlugLength" /> and trims a trailing <c>-</c> the cut exposed.
    /// </summary>
    private static string Slug(string token)
    {
        var builder = new StringBuilder(token.Length);
        foreach (char character in token)
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
                builder.Append(character);
            else if (builder.Length == 0 || builder[^1] != '-')
                builder.Append('-');
        }

        string slug = builder.ToString().Trim('-');
        return slug.Length <= MaxSlugLength ? slug : slug[..MaxSlugLength].TrimEnd('-');
    }

    private static string HashedStem(string token, string slug)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..HashLength];
        return slug.Length > 0 ? $"{Prefix}{slug}-{hash}" : Prefix + hash;
    }
}
