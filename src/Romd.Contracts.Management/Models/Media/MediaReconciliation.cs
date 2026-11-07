using System.Text.Json.Serialization;

namespace Romd.Contracts.Management.Models.Media;

public sealed record StartMediaReconciliationSubjectRequest([property: JsonRequired] Guid RequestId, [property: JsonRequired] bool ConversionRequested);
public sealed record StartMediaReconciliationSelectionRequest([property: JsonRequired] Guid RequestId, [property: JsonRequired] string Filter, string? KindId,
    string? SystemKey, [property: JsonRequired] long Limit, [property: JsonRequired] bool ConversionRequested);
public sealed record RetryMediaReconciliationRequest([property: JsonRequired] Guid RequestId);

public sealed record MediaReconciliationPolicy(string KindId, string RecipeId, string ConversionMode,
    IReadOnlyList<string> SystemKeys, string ManifestType, string StoredFormatLabel, bool CanDecode, string? QualifiedRuntime);
