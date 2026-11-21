using ErrorOr;
using Romd.Contracts.Management.Libraries;
using Romd.Domain.Libraries;

using LibraryConfiguration = Romd.Domain.Libraries.LibraryConfiguration;

namespace Romd.Admin.Application.Libraries;

public interface ILibraryDraftEvaluator
{
    Task<ErrorOr<LibraryEvaluation>> EvaluateAsync(int libraryId, LibraryConfiguration configuration,
        string view, string? search, int afterId, CancellationToken ct, string? cursor = null, int limit = 48);

    /// <summary>Compares the saved rules and published membership within the caller's read snapshot.</summary>
    Task<ErrorOr<LibraryTitleVisibility>> GetTitleVisibilityAsync(int libraryId, int titleId, CancellationToken ct);
}
