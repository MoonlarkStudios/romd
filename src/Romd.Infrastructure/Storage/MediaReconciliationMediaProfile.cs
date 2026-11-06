using System.Security.Cryptography;
using System.Text;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Media.Inspection;

namespace Romd.Infrastructure.Storage;

/// <summary>Actual worker codec identities and validated processing limits, without changing consumer read capabilities.</summary>
public sealed class MediaReconciliationMediaProfile : IMediaReconciliationMediaProfile
{
    private readonly ChdDeploymentRuntime _deployment;
    private readonly IMediaHostCapabilities _capabilities;
    private readonly MediaKindCatalog _kinds;
    private readonly IReadOnlyDictionary<string, RomWholeEncodingProfile> _whole;
    public MediaReconciliationMediaProfile(MediaProcessingOptions limits, TempStorageOptions staging, IRomdOptions application, ChdDeploymentRuntime deployment, RomMediaComposition composition, IMediaHostCapabilities capabilities)
    {
        var captured = limits.ValidatedCopy();
        _deployment = deployment;
        _capabilities = capabilities;
        _kinds = composition.Kinds;
        _whole = composition.WholeEncoding;
        var temporary = staging.ValidatedCopy(application.MaxUploadBytes);
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("media-reconciliation-limits/v2");
            writer.Write(application.MaxUploadBytes);
            writer.Write(temporary.MaximumFiles);
            writer.Write(temporary.MaximumBytes);
            writer.Write(temporary.MinimumFreeBytes);
            writer.Write(temporary.AdmissionTimeoutSeconds);
            writer.Write(temporary.CopyTimeoutSeconds);
            writer.Write(captured.MaxArtifactBytes);
            writer.Write(captured.MaxLogicalBytes);
            writer.Write(captured.MaxConcurrent);
            writer.Write(captured.AdmissionTimeoutSeconds);
            writer.Write(captured.WallTimeoutSeconds);
            writer.Write(captured.CpuTimeoutSeconds);
            writer.Write(captured.MemoryBytes);
            writer.Write(captured.LinuxAddressSpaceBytes);
            writer.Write(captured.LinuxDataBytes);
            writer.Write(captured.ChdEncodeCpuTimeoutSeconds);
            writer.Write(captured.ChdEncodeWallTimeoutSeconds);
        }
        LimitsFingerprint = Sha256.FromSpan(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
    }
    public Sha256 LimitsFingerprint { get; }
    public RomDecoderIdentity.Managed OriginalInspectionDecoder => new(MediaInspectionService.DecoderBuild);
    public MediaReconciliationEncoder? Encoder(string kind)
    {
        if (!_capabilities.CanEncode(kind) || _kinds.Find(kind) is not { } descriptor)
            return null;
        _whole.TryGetValue(kind, out var whole);
        RomIsoDiscKind? discKind = whole?.DiscKind;
        if (descriptor.StoredEncoding == RomRepresentationEncoding.Chd)
            return _deployment.IsReady && _deployment.Assets?.Tool?.ExpectedIdentity is { } tool
                ? new(ChdBuildEvidence.Encoder(tool).BuildLabel, descriptor.ManifestType == MediaManifestType.WholeFile ? discKind : null) : null;
        if (!_whole.ContainsKey(kind))
            return null;
        return RvzDiscFamily.For(discKind) is { } family ? new(family.Codec.EncoderBuild, discKind) : null;
    }
}
