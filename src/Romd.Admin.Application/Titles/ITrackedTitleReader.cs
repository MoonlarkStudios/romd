namespace Romd.Admin.Application.Titles;

/// <summary>Read port for current title tracking intent.</summary>
public interface ITrackedTitleReader
{
    /// <summary>
    ///     Whether any title is tracked or, when <paramref name="titleIds" /> is given, whether any of
    ///     those titles is tracked.
    /// </summary>
    Task<bool> HasTrackedAsync(IReadOnlyCollection<int>? titleIds = null, CancellationToken cancellationToken = default);
}
