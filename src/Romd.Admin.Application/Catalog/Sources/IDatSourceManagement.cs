using ErrorOr;

namespace Romd.Admin.Application.Catalog.Sources;

public sealed record SourceRemovalImpact(string Name, string Action, string ReviewToken, int Versions,
    int StillCovered, int OwnedWithoutDefinition, int PersonalWithoutDefinition, int CatalogOnlyWithoutDefinition,
    bool Busy);

public interface IDatSourceManagement
{
    Task<ErrorOr<SourceRemovalImpact>> PreviewAsync(int datId, string action, CancellationToken ct);
    Task<ErrorOr<Success>> ApplyAsync(int datId, string action, string reviewToken, CancellationToken ct);
}
