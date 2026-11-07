using Romd.Contracts.Common.Models;

namespace Romd.Contracts.Consumer.Releases;

public sealed record ConsumerDiscSetSummary(string DiscSetId, Guid Revision, string Name, int? ExpectedDiscCount,
    IReadOnlyList<string> ReleaseIds);
public sealed record ConsumerDiscSetManifest(string ServerInstanceId, string DiscSetId, Guid Revision,
    string DeliverySnapshotId, string TitleId, string SystemKey, string Name, int? ExpectedDiscCount,
    bool HasAllExpectedDiscs, IReadOnlyList<ConsumerDiscSetDisc> Discs, ConsumerDerivedPlaylist Playlist)
{
    public int ContractVersion { get; init; } = 1;
    public string ObservationId { get; init; } = string.Empty;
    public ConsumerDiscSetSelection? Selection { get; init; }
}
public sealed record ConsumerDiscSetDisc(string ReleaseId, int Ordinal, string Name, string EntryPoint,
    IReadOnlyList<ConsumerDiscSetFile> Files)
{
    public string? LayoutId { get; init; }
    public string? LayoutRevision { get; init; }
}
public sealed record ConsumerDiscSetFile(string RelativePath, ByteCount SizeBytes, string Sha256, ConsumerReleaseFileProvenance Provenance, ContentGrant ContentGrant);
// Inline text is the derived artifact: encode exactly as UTF-8, without BOM. Clients verify its hash like every other file.
public sealed record ConsumerDerivedPlaylist(string ArtifactId, string RelativePath, string Sha256, ByteCount SizeBytes,
    string Encoding, string Text);


public sealed record ConsumerDiscSetStatus(int ContractVersion, string ServerInstanceId, string DiscSetId,
    Guid Revision, string TitleId, string SystemKey, string Name, string ObservationId, int? ExpectedDiscCount,
    IReadOnlyList<int> MissingExpectedOrdinals, IReadOnlyList<ConsumerDiscSetStatusDisc> Discs);
public sealed record ConsumerDiscSetStatusDisc(string ReleaseId, int Ordinal, string Name,
    string? LayoutId, string? LayoutRevision, string Readiness);
public sealed record ConsumerDiscSetSelectionRequest(int ContractVersion, Guid Revision, string ObservationId,
    IReadOnlyList<string> SelectedReleaseIds, bool AllowPartial, string? RequiredSnapshotId = null);
public sealed record ConsumerDiscSetSelection(string Kind, string Completeness, IReadOnlyList<string> SelectedReleaseIds,
    IReadOnlyList<ConsumerDiscSetOmittedDisc> OmittedDiscs, IReadOnlyList<int> MissingExpectedOrdinals);
public sealed record ConsumerDiscSetOmittedDisc(string ReleaseId, int Ordinal);
public sealed record ConsumerDiscSetLayoutVersion(string ReleaseId, string? LayoutId, string? LayoutRevision);
public sealed record ConsumerDiscSetAccessRequest(int ContractVersion, string ServerInstanceId, Guid Revision,
    string DeliverySnapshotId, IReadOnlyList<string> SelectedReleaseIds,
    IReadOnlyList<ConsumerDiscSetLayoutVersion> LayoutVersions, bool AllowPartial);
public sealed record ConsumerDiscSetAccess(int ContractVersion, string ServerInstanceId, string DiscSetId,
    Guid Revision, string DeliverySnapshotId, IReadOnlyList<string> SelectedReleaseIds,
    IReadOnlyList<ConsumerDiscSetLayoutVersion> LayoutVersions, bool Allowed, string? TitleId, string? SystemKey);
