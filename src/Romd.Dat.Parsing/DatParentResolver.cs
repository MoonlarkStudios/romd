namespace Romd.Dat.Parsing;

public sealed record DatParentDeclaration(string Name, string? DeclaredId, string? CloneOf, string? CloneOfId);

public enum DatParentStatus
{
    Resolved,
    Missing,
    Ambiguous,
    Conflicting,
    Cycle
}

public sealed record DatParentResolution(DatParentStatus Status, string? ParentName);

/// <summary>
///     Resolves explicit clone parent declarations within a single DAT document.
/// </summary>
public static class DatParentResolver
{
    /// <summary>
    ///     Returns a result for each entry that declares a clone parent by name or ID.
    ///     Names must be unique. Nonblank names and IDs use ordinal comparison.
    ///     A reference to a duplicated ID is ambiguous. If both references are present,
    ///     each must independently resolve to the same entry. Blank references are missing.
    ///     A resolved link whose ancestry reaches a cycle is reported as cyclic.
    /// </summary>
    public static IReadOnlyDictionary<string, DatParentResolution> Resolve(
        IReadOnlyCollection<DatParentDeclaration> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        cancellationToken.ThrowIfCancellationRequested();

        var names = new HashSet<string>(entries.Count, StringComparer.Ordinal);
        var namesById = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!names.Add(entry.Name))
            {
                throw new ArgumentException("DAT entries must have unique names.", nameof(entries));
            }

            if (!string.IsNullOrWhiteSpace(entry.DeclaredId) && !namesById.TryAdd(entry.DeclaredId, entry.Name))
            {
                namesById[entry.DeclaredId] = null;
            }
        }

        var resolutions = new Dictionary<string, DatParentResolution>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.CloneOf is not null || entry.CloneOfId is not null)
            {
                resolutions.Add(entry.Name, ResolveDirectParent(entry, names, namesById));
            }
        }

        MarkCycles(entries, resolutions, cancellationToken);
        return resolutions.AsReadOnly();
    }

    private static DatParentResolution ResolveDirectParent(
        DatParentDeclaration entry,
        HashSet<string> names,
        Dictionary<string, string?> namesById)
    {
        string? parentById = null;
        if (entry.CloneOfId is not null)
        {
            if (string.IsNullOrWhiteSpace(entry.CloneOfId) || !namesById.TryGetValue(entry.CloneOfId, out parentById))
            {
                return new(DatParentStatus.Missing, null);
            }

            if (parentById is null)
            {
                return new(DatParentStatus.Ambiguous, null);
            }
        }

        if (entry.CloneOf is null)
        {
            return new(DatParentStatus.Resolved, parentById);
        }

        if (string.IsNullOrWhiteSpace(entry.CloneOf) || !names.Contains(entry.CloneOf))
        {
            return new(DatParentStatus.Missing, null);
        }

        if (parentById is not null && !StringComparer.Ordinal.Equals(parentById, entry.CloneOf))
        {
            return new(DatParentStatus.Conflicting, null);
        }

        return new(DatParentStatus.Resolved, entry.CloneOf);
    }

    private static void MarkCycles(
        IReadOnlyCollection<DatParentDeclaration> entries,
        Dictionary<string, DatParentResolution> resolutions,
        CancellationToken ct)
    {
        var visitedInTraversal = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = new List<string>();
        var cyclic = new DatParentResolution(DatParentStatus.Cycle, null);
        int traversal = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            traversal++;
            string name = entry.Name;
            bool reachesCycle = false;
            while (resolutions.TryGetValue(name, out var resolution))
            {
                ct.ThrowIfCancellationRequested();
                if (resolution.Status != DatParentStatus.Resolved)
                {
                    reachesCycle = resolution.Status == DatParentStatus.Cycle;
                    break;
                }

                // Revisiting an entry in this traversal closes a loop. Earlier traversals
                // have already marked any cyclic ancestry in the resolution dictionary.
                if (!visitedInTraversal.TryAdd(name, traversal))
                {
                    reachesCycle = visitedInTraversal[name] == traversal;
                    break;
                }

                path.Add(name);
                name = resolution.ParentName!;
            }

            if (reachesCycle)
            {
                foreach (string ancestor in path)
                {
                    ct.ThrowIfCancellationRequested();
                    resolutions[ancestor] = cyclic;
                }
            }

            path.Clear();
        }
    }
}
