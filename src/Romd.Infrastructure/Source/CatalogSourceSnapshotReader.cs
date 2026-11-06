using System.Collections.Immutable;
using Romd.Admin.Application.Catalog;

namespace Romd.Infrastructure.Source;

/// <summary>
///     Composes provider-owned source facts into the catalog's immutable read snapshot. Providers
///     run sequentially because they intentionally share the scoped context and caller transaction.
/// </summary>
public sealed class CatalogSourceSnapshotReader(
    IEnumerable<ICatalogSourceSnapshotProvider> providers) : ICatalogSourceSnapshotReader
{
    private const int ProviderKeyMaxLength = 200;
    private const string ReservedMigrationKeyPrefix = "~romd-migration-";

    public async Task<CatalogSourceSnapshot> ReadPlatformAsync(
        int platformId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = ImmutableArray.CreateBuilder<CatalogSourceEntrySnapshot>();
        var sourceEntryOwnerIndexes = new Dictionary<int, int>();
        int providerIndex = 0;

        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await provider.ReadPlatformAsync(platformId, cancellationToken);

            foreach (var entry in snapshot.Entries)
            {
                int sourceEntryId = entry.SourceEntryId;
                if (sourceEntryOwnerIndexes.TryGetValue(sourceEntryId, out int ownerIndex))
                {
                    throw new InvalidOperationException(
                        ownerIndex == providerIndex
                            ? $"Catalog source entry {sourceEntryId} was returned more than once by snapshot provider {providerIndex}."
                            : $"Catalog source entry {sourceEntryId} was asserted by snapshot providers {ownerIndex} and {providerIndex}.");
                }

                sourceEntryOwnerIndexes[sourceEntryId] = providerIndex;
                ValidateEntry(entry);
            }

            entries.AddRange(snapshot.Entries);
            providerIndex++;
        }

        return entries.Count == 0
            ? CatalogSourceSnapshot.Empty
            : new CatalogSourceSnapshot(entries
                .OrderBy(entry => entry.SourceKind)
                .ThenBy(entry => entry.SourceEntryId)
                .Select(entry => entry with
                {
                    Claims = entry.Claims
                        .OrderBy(claim => claim.ClaimPrecedence)
                        .ThenByDescending(claim => claim.ClaimOrder)
                        .ThenBy(claim => claim.ProviderClaimKey, StringComparer.Ordinal)
                        .ToImmutableArray()
                })
                .ToImmutableArray());
    }

    private static void ValidateEntry(CatalogSourceEntrySnapshot entry)
    {
        if (entry.SourceEntryId <= 0)
        {
            throw new InvalidOperationException("Catalog source entry id must be positive.");
        }

        if (!Enum.IsDefined(entry.SourceKind))
        {
            throw new InvalidOperationException(
                $"Catalog source entry {entry.SourceEntryId} has undefined source kind '{entry.SourceKind}'.");
        }

        var claimKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in entry.Claims)
        {
            ValidateKey(claim.ProviderClaimKey, nameof(claim.ProviderClaimKey));
            if (!claimKeys.Add(claim.ProviderClaimKey))
            {
                throw new InvalidOperationException(
                    $"Catalog source entry {entry.SourceEntryId} contains duplicate provider claim key '{claim.ProviderClaimKey}'.");
            }

            var requirementKeys = new HashSet<(CatalogSourceRequirementKind Kind, string Key)>(
                RequirementKeyComparer.Instance);
            foreach (var requirement in claim.Requirements)
            {
                if (!Enum.IsDefined(requirement.Kind))
                {
                    throw new InvalidOperationException(
                        $"Provider claim '{claim.ProviderClaimKey}' has undefined requirement kind '{requirement.Kind}'.");
                }

                ValidateKey(requirement.ProviderRequirementKey, nameof(requirement.ProviderRequirementKey));
                if (!requirementKeys.Add((requirement.Kind, requirement.ProviderRequirementKey)))
                {
                    throw new InvalidOperationException(
                        $"Provider claim '{claim.ProviderClaimKey}' contains duplicate {requirement.Kind} requirement key '{requirement.ProviderRequirementKey}'.");
                }
            }
        }
    }

    private static void ValidateKey(string value, string name)
    {
        if (string.IsNullOrEmpty(value) || value.Length > ProviderKeyMaxLength)
        {
            throw new InvalidOperationException($"{name} must contain between 1 and {ProviderKeyMaxLength} characters.");
        }

        if (value.StartsWith(ReservedMigrationKeyPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{name} uses the reserved catalog migration prefix '{ReservedMigrationKeyPrefix}'.");
        }
    }

    private sealed class RequirementKeyComparer : IEqualityComparer<(CatalogSourceRequirementKind Kind, string Key)>
    {
        public static RequirementKeyComparer Instance { get; } = new();

        public bool Equals(
            (CatalogSourceRequirementKind Kind, string Key) x,
            (CatalogSourceRequirementKind Kind, string Key) y) =>
            x.Kind == y.Kind && StringComparer.Ordinal.Equals(x.Key, y.Key);

        public int GetHashCode((CatalogSourceRequirementKind Kind, string Key) value) =>
            HashCode.Combine(value.Kind, StringComparer.Ordinal.GetHashCode(value.Key));
    }
}
