using ErrorOr;
using Romd.Contracts.Management.Libraries;
using Romd.Domain.Libraries;

namespace Romd.Admin.Application.Libraries;

public interface ILibraryDraftEvaluator
{
    Task<ErrorOr<LibraryEvaluationDto>> EvaluateAsync(int libraryId, LibraryConfiguration configuration,
        string view, string? search, int afterId, CancellationToken ct);
}
