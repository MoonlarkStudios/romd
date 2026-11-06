using Romd.Domain.Source.Rom;
using ErrorOr;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Media.Rvz;

namespace Romd.Infrastructure.Storage;

/// <summary>An owned encoded artifact and raw-byte metadata; separate inspection must establish ROM evidence.</summary>
public sealed record RvzEncodedArtifact(ITempFile Artifact, TempFileArtifactMeasurement Measurement,
    string RecipeVersion, string EncoderBuild, string EncodingProfile);

/// <summary>Shares inspection admission and aggregate staging accounting with supervised encoding.</summary>
public sealed class RvzEncodingService
{
    private readonly RvzVerifier _admission;
    private readonly ITempFileFactory _files;
    private readonly MediaProcessingOptions _options;
    public RvzEncodingService(RvzVerifier admission, ITempFileFactory files, MediaProcessingOptions options)
    {
        _admission = admission;
        _files = files;
        _options = options.ValidatedCopy();
    }
    /// <summary>Disc kind comes from independent eligibility; the child rechecks the complete source layout.</summary>
    public async Task<ErrorOr<RvzEncodedArtifact>> EncodeAsync(ITempFile source, long logicalBytes, RomIsoDiscKind discKind, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (RvzDiscFamily.For(discKind) is not { } family)
            return MediaInspectionErrors.Failed(MediaWorkerFailure.Unsupported);
        var bound = family.GetOutputBound(logicalBytes, _options.MaxLogicalBytes, ct);
        if (bound is not RvzCanonicalOutputBound.Success accepted || accepted.MaximumOutputBytes > _options.MaxArtifactBytes ||
            source.FileSize < 4 || source.FileSize > _options.MaxArtifactBytes)
            return MediaInspectionErrors.Failed(MediaWorkerFailure.LimitExceeded);
        using var admission = await _admission.AcquireAsync(ct);
        var reserved = await _files.ReserveOutputAsync(accepted.MaximumOutputBytes, ct);
        if (reserved.IsError)
            return reserved.Errors;
        await using var output = reserved.Value;
        var encoded = await admission.EncodeAsync(source, output, logicalBytes, discKind, ct);
        if (encoded.IsError)
            return encoded.Errors;
        var facts = encoded.Value;
        var measurement = new TempFileArtifactMeasurement(Sha256.Parse(facts.ArtifactSha256), Md5.Parse(facts.ArtifactMd5),
            Sha1.Parse(facts.ArtifactSha1), facts.ArtifactBytes);
        var sealedFile = await output.SealAsync(measurement, ct);
        if (sealedFile.IsError)
            return sealedFile.Errors;
        return new RvzEncodedArtifact(sealedFile.Value, measurement, facts.RecipeVersion, facts.EncoderBuild, facts.EncodingProfile);
    }
}
