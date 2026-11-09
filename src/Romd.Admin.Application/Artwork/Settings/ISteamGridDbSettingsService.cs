using ErrorOr;
using Romd.Contracts.Management.Artwork;
using Romd.Application.Common.Concurrency;

namespace Romd.Admin.Application.Artwork.Settings;

public interface ISteamGridDbSettingsService
{
    Task<SteamGridDbSettings> GetAsync(CancellationToken ct = default);
    /// <summary>Stages an accepted replacement in the command's transaction, without committing.</summary>
    Task<ErrorOr<SteamGridDbSettings>> StageUpdateAsync(UpdateSteamGridDbSettingsRequest request, RevisionPrecondition ifMatch, CancellationToken ct = default);
    /// <summary>Checks the precondition before external I/O. It owns no database transaction.</summary>
    Task<ErrorOr<SteamGridDbTestResult>> ProbeAsync(RevisionPrecondition ifMatch, CancellationToken ct = default);
    /// <summary>Stages the test status only while its probed revision is current, rotating the revision.</summary>
    Task<ErrorOr<SteamGridDbSettings>> StageTestAsync(SteamGridDbTestResult result, CancellationToken ct = default);
}

public sealed record SteamGridDbTestResult(Guid ExpectedRevision, DateTimeOffset TestedAt, bool Succeeded,
    string Message, string ConfigurationFingerprint);
