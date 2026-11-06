using Romd.Contracts.Common.Models;

namespace Romd.Contracts.Consumer.Releases;

public sealed record ConsumerDiscSetSummaryDto(string DiscSetId, Guid Revision, string Name, int? ExpectedDiscCount,
    IReadOnlyList<string> ReleaseIds);
public sealed record ConsumerDiscSetManifestDto(string ServerInstanceId, string DiscSetId, Guid Revision,
    string DeliverySnapshotId, string TitleId, string SystemKey, string Name, int? ExpectedDiscCount,
    bool HasAllExpectedDiscs, IReadOnlyList<ConsumerDiscSetDiscDto> Discs, ConsumerDerivedPlaylistDto Playlist)
{
    public int ContractVersion { get; init; } = 1;
    public string ObservationId { get; init; } = string.Empty;
    public ConsumerDiscSetSelectionDto? Selection { get; init; }
}
public sealed record ConsumerDiscSetDiscDto(string ReleaseId, int Ordinal, string Name, string EntryPoint,
    IReadOnlyList<ConsumerDiscSetFileDto> Files)
{
    public string? LayoutId { get; init; }
    public string? LayoutRevision { get; init; }
}
public sealed record ConsumerDiscSetFileDto(string RelativePath, ByteCount SizeBytes, string Sha256, ConsumerReleaseFileProvenance Provenance, ContentGrantDto ContentGrant);
// Inline text is the derived artifact: encode exactly as UTF-8, without BOM. Clients verify its hash like every other file.
public sealed record ConsumerDerivedPlaylistDto(string ArtifactId, string RelativePath, string Sha256, ByteCount SizeBytes,
    string Encoding, string Text);


public sealed record ConsumerDiscSetStatusDto(int ContractVersion, string ServerInstanceId, string DiscSetId,
    Guid Revision, string TitleId, string SystemKey, string Name, string ObservationId, int? ExpectedDiscCount,
    IReadOnlyList<int> MissingExpectedOrdinals, IReadOnlyList<ConsumerDiscSetStatusDiscDto> Discs);
public sealed record ConsumerDiscSetStatusDiscDto(string ReleaseId, int Ordinal, string Name,
    string? LayoutId, string? LayoutRevision, string Readiness);
public sealed record ConsumerDiscSetSelectionRequest(int ContractVersion, Guid Revision, string ObservationId,
    IReadOnlyList<string> SelectedReleaseIds, bool AllowPartial, string? RequiredSnapshotId = null);
public sealed record ConsumerDiscSetSelectionDto(string Kind, string Completeness, IReadOnlyList<string> SelectedReleaseIds,
    IReadOnlyList<ConsumerDiscSetOmittedDiscDto> OmittedDiscs, IReadOnlyList<int> MissingExpectedOrdinals);
public sealed record ConsumerDiscSetOmittedDiscDto(string ReleaseId, int Ordinal);
public sealed record ConsumerDiscSetLayoutVersionDto(string ReleaseId, string? LayoutId, string? LayoutRevision);
public sealed record ConsumerDiscSetAccessRequest(int ContractVersion, string ServerInstanceId, Guid Revision,
    string DeliverySnapshotId, IReadOnlyList<string> SelectedReleaseIds,
    IReadOnlyList<ConsumerDiscSetLayoutVersionDto> LayoutVersions, bool AllowPartial);
public sealed record ConsumerDiscSetAccessDto(int ContractVersion, string ServerInstanceId, string DiscSetId,
    Guid Revision, string DeliverySnapshotId, IReadOnlyList<string> SelectedReleaseIds,
    IReadOnlyList<ConsumerDiscSetLayoutVersionDto> LayoutVersions, bool Allowed, string? TitleId, string? SystemKey);
