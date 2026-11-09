using ErrorOr;
using Romd.Application.Common.Concurrency;
using Romd.Contracts.Management.MetadataProviders;

namespace Romd.Admin.Application.MetadataProviders;

public interface IIgdbProviderSettingsService
{
    Task<IgdbProviderSettings> GetAsync(CancellationToken ct = default);
    Task<ErrorOr<IgdbProviderSettings>> UpdateStagedAsync(UpdateIgdbProviderSettingsRequest request, RevisionPrecondition precondition, CancellationToken ct = default);
    Task<ErrorOr<IgdbProviderTestObservation>> ProbeAsync(RevisionPrecondition precondition, CancellationToken ct = default);
    Task<ErrorOr<IgdbProviderSettings>> RecordTestStagedAsync(IgdbProviderTestObservation observation, CancellationToken ct = default);
}

// Contains sanitized test facts only, never credentials or upstream bodies.
public sealed record IgdbProviderTestObservation(Guid Revision, DateTimeOffset TestedAt, bool Succeeded, string Message, string ConfigurationFingerprint);
