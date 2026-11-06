using ErrorOr;
using Romd.Application.Common.Concurrency;
using Romd.Contracts.Management.MetadataProviders;

namespace Romd.Admin.Application.MetadataProviders;

public interface IIgdbProviderSettingsService
{
    Task<IgdbProviderSettingsDto> GetAsync(CancellationToken ct = default);
    Task<ErrorOr<IgdbProviderSettingsDto>> UpdateStagedAsync(UpdateIgdbProviderSettingsRequest request, RevisionPrecondition precondition, CancellationToken ct = default);
    Task<ErrorOr<IgdbProviderTestObservation>> ProbeAsync(RevisionPrecondition precondition, CancellationToken ct = default);
    Task<ErrorOr<IgdbProviderSettingsDto>> RecordTestStagedAsync(IgdbProviderTestObservation observation, CancellationToken ct = default);
}

// Contains sanitized test facts only, never credentials or upstream bodies.
public sealed record IgdbProviderTestObservation(Guid Revision, DateTimeOffset TestedAt, bool Succeeded, string Message, string ConfigurationFingerprint);
