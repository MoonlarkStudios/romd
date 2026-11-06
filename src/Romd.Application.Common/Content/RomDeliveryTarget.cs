using ErrorOr;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Application.Common.Content;

/// <summary>Immutable delivered bytes and their logical/artifact bindings, after authorization.</summary>
public sealed record RomDeliveryTarget(int RomId, int FileId, RomDeliveryFormat Format, string Filename,
    Sha256 Sha256, long SizeBytes, Sha256 ContentSha256, long ContentSizeBytes,
    Sha256 ArtifactSha256, long ArtifactSizeBytes, RomRepresentationEncoding Encoding,
    Md5? Md5 = null, Sha1? Sha1 = null, RomReadBinding? ReadBinding = null);

public sealed record RomDeliveryOption(RomDeliveryFormat Format, RomDeliveryState State,
    RomDeliveryTarget? Target, string ProfileVersion, IReadOnlyList<string> RuntimeApplicability);

public static class RomDeliveryFormats
{
    public static ErrorOr<RomDeliveryFormat> Parse(string? value) => value switch
    {
        "original" => RomDeliveryFormat.Original,
        "rvz" => RomDeliveryFormat.Rvz,
        "chd" => RomDeliveryFormat.Chd,
        _ => RomDeliveryErrors.InvalidFormat()
    };

    public static string ToWire(RomDeliveryFormat format) => format switch
    {
        RomDeliveryFormat.Original => "original",
        RomDeliveryFormat.Rvz => "rvz",
        RomDeliveryFormat.Chd => "chd",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    public static string ToWire(RomDeliveryState state) => state switch
    {
        RomDeliveryState.Available => "available",
        RomDeliveryState.Preparing => "preparing",
        RomDeliveryState.Unsupported => "unsupported",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };
}

public static class RomDeliveryTargets
{
    public static bool IsValid(RomDeliveryTarget target) => target.RomId > 0 && target.FileId > 0
        && !string.IsNullOrWhiteSpace(target.Filename) && !target.Sha256.IsEmpty && target.SizeBytes >= 0
        && !target.ContentSha256.IsEmpty && target.ContentSizeBytes >= 0
        && !target.ArtifactSha256.IsEmpty && target.ArtifactSizeBytes >= 0
        && target.Encoding is RomRepresentationEncoding.Identity or RomRepresentationEncoding.Rvz or RomRepresentationEncoding.Chd
        && (target.Encoding switch
        {
            RomRepresentationEncoding.Chd => target.ReadBinding switch
            {
                RomChdDeliveryBinding { RepresentationId: > 0, OutputId: > 0, Decoder: not null, MeasurementId: var id } => id != Guid.Empty,
                RomCdMemberBinding { RepresentationId: > 0, OutputId: > 0, Ordinal: >= 0 and <= 198, Decoder: not null } binding =>
                    binding.MeasurementId != Guid.Empty && target.Format == RomDeliveryFormat.Original && binding.ArtifactSha256 == target.ArtifactSha256,
                _ => false
            },
            RomRepresentationEncoding.Rvz => target.ReadBinding is RomManagedReadBinding { RepresentationId: > 0, OutputId: > 0 } managed &&
                managed.MeasurementId != Guid.Empty && !string.IsNullOrWhiteSpace(managed.DecoderBuild),
            RomRepresentationEncoding.Identity => target.ReadBinding is null,
            _ => false
        })
        && (target.Encoding != RomRepresentationEncoding.Identity
            || target.ArtifactSha256 == target.ContentSha256 && target.ArtifactSizeBytes == target.ContentSizeBytes)
        && (target.Format switch
        {
            RomDeliveryFormat.Original => target.Sha256 == target.ContentSha256 && target.SizeBytes == target.ContentSizeBytes,
            RomDeliveryFormat.Rvz => target.Encoding == RomRepresentationEncoding.Rvz
                && target.Sha256 == target.ArtifactSha256 && target.SizeBytes == target.ArtifactSizeBytes,
            RomDeliveryFormat.Chd => target.Encoding == RomRepresentationEncoding.Chd
                && target.Md5 is { IsEmpty: false } && target.Sha1 is { IsEmpty: false }
                && target.Sha256 == target.ArtifactSha256 && target.SizeBytes == target.ArtifactSizeBytes,
            _ => false
        });
}
