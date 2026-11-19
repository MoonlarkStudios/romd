using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Models;
using Romd.Domain.Source.Platform;

namespace Romd.Admin.Application.ReferenceData.Systems.Queries.PreviewSystemNameAliases;

public sealed record PreviewSystemNameAliasesQuery(IReadOnlyList<string>? NameAliases) : IQuery<SystemNameAliasConflict[]>;

public sealed class PreviewSystemNameAliasesQueryHandler(ISystemReferenceRepository repository)
    : IQueryHandler<PreviewSystemNameAliasesQuery, SystemNameAliasConflict[]>
{
    public async Task<ErrorOr<SystemNameAliasConflict[]>> HandleAsync(PreviewSystemNameAliasesQuery query, CancellationToken ct = default)
    {
        if (query.NameAliases is not { Count: > 0 } names || !ReferenceEditRules.SetupAliases(names))
            return ReferenceEditErrors.Invalid("Enter up to 16 name aliases of at most 200 characters each, without control characters or leading or trailing spaces.");

        var normalizedNames = names.Select(PlatformNameNormalizer.Normalize).Distinct(StringComparer.Ordinal).ToArray();
        return await repository.FindSetupNameAliasConflictsAsync(normalizedNames, ct);
    }
}
