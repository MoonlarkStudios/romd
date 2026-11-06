using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom.Reconciliation;

/// <summary>Derives one operation from caller-supplied current facts, without reading persistence or media payloads.</summary>
public sealed class MediaReconciliationDecision
{
    private readonly MediaKindCatalog _kinds;
    private readonly IReadOnlyDictionary<string, MediaReconciliationPolicy> _policies;
    private readonly IMediaHostCapabilities _capabilities;
    private readonly IMediaReconciliationMediaProfile _media;
    public MediaReconciliationDecision(MediaKindCatalog kinds, IReadOnlyDictionary<string, MediaReconciliationPolicy> policies,
        IMediaHostCapabilities capabilities, IMediaReconciliationMediaProfile media)
    {
        this._kinds = kinds;
        this._policies = policies;
        this._capabilities = capabilities;
        this._media = media;
    }
    public ErrorOr<MediaReconciliationDirective> Decide(Guid jobId, MediaReconciliationDemandState demand, MediaReconciliationFacts facts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(demand);
        ArgumentNullException.ThrowIfNull(facts);
        ct.ThrowIfCancellationRequested();
        if (facts is MediaReconciliationFacts.None absent)
            return new MediaReconciliationDirective.Settle(MediaReconciliationOutcomeKind.Superseded, absent.Code);
        if (facts is MediaReconciliationFacts.Representation retained)
        {
            var graph = retained.Graph;
            if (graph.Container is RomContainerFacts.Identity)
                return Complete("original-bytes");
            string? qualifiedKind = null;
            if (graph.Manifest is RomReconstructionManifest.CdTracks)
            {
                var matches = _policies.Values.Where(policy => policy.MatchesRetainedLayout(graph)).ToArray();
                if (matches.Length != 1)
                    return new MediaReconciliationDirective.Unavailable(matches.Length == 0 ? "cd-kind-unregistered" : "cd-kind-ambiguous");
                qualifiedKind = matches[0].Kind.Kind;
            }
            var decoder = Decoder(graph.Container.Encoding, qualifiedKind);
            if (decoder is null)
                return new MediaReconciliationDirective.Unavailable("decoder-unavailable");
            if (graph.Verifications.Any(proof => proof.Decoder.Equals(decoder)))
                return Complete("verified");
            return Verify(graph, decoder, new([new MediaReconciliationInput.Artifact(graph, null, null)], null, [], null), null);
        }
        if (facts is MediaReconciliationFacts.Layout layout)
        {
            if (!_policies.TryGetValue(layout.KindId, out var policy))
                return Complete("kind-unregistered");
            var dependencies = layout.Sources.Inputs.OfType<MediaReconciliationInput.Logical>().Select(input => input.Graph).DistinctBy(graph => graph.Id).ToArray();
            foreach (var dependency in dependencies.Where(graph => graph.Container.Encoding.IsEncoded()))
            {
                var resident = Decoder(dependency.Container.Encoding, layout.KindId);
                if (resident is null)
                    return new MediaReconciliationDirective.Unavailable("decoder-unavailable");
                if (!dependency.Verifications.Any(proof => proof.Decoder.Equals(resident)))
                    return Verify(dependency, resident, new([new MediaReconciliationInput.Artifact(dependency, null, null)], layout.Sources.Layout, [], null), layout.KindId);
            }
            if (layout.Sources.Layout is MediaReconciliationLayout.Original && layout.AdoptionCandidate is { } candidate &&
                policy.MatchesParameters(candidate.Graph.Container) && !layout.Archives.Any(graph => policy.MatchesParameters(graph.Container)))
            {
                var decoder = Decoder(candidate.Graph.Container.Encoding, layout.KindId);
                if (decoder is null)
                    return new MediaReconciliationDirective.Unavailable("decoder-unavailable");
                var held = IncludeHoldings(layout.Sources, layout.Archives, null, null);
                var adoptionInputs = held.Inputs.Where(input => input is not MediaReconciliationInput.Artifact artifact || artifact.Graph.Id != candidate.Graph.Id)
                    .Append(candidate).ToArray();
                var sources = new MediaReconciliationSources(adoptionInputs, held.Layout, held.LogicalInspectionIds, held.ReferenceFingerprint);
                return new MediaReconciliationDirective.Adopt(Capture(MediaReconciliationOperation.Adopt, sources, decoder, layout.KindId, policy, null));
            }
            // A different layout's archive may supply BIN bytes; it is never this layout's owned canonical target.
            var holdings = dependencies.Concat(layout.Sources.Inputs.OfType<MediaReconciliationInput.Artifact>().Select(input => input.Graph))
                .Where(graph => graph.Manifest is RomReconstructionManifest.WholeFile).Concat(layout.Archives).DistinctBy(graph => graph.Id).ToArray();
            var actual = new MediaReconciliationState(holdings, layout.CurrentArchiveId, encodingEligible: true, layout.OwnershipNeedsAlignment);
            return Decide(policy, actual, layout.Sources, null);
        }
        var logical = (MediaReconciliationFacts.Logical)facts;
        var content = logical.Content;
        var resolved = MediaKindResolver.Resolve(_kinds, content, logical.PlatformKeys);
        if (resolved.Unique?.ManifestType == MediaManifestType.CdTracks || content.Preferred.Facts.Manifest is RomReconstructionManifest.CdTracks)
            return Complete("layout-managed");
        var whole = content.Artifacts.Where(artifact => artifact.Facts.Manifest is RomReconstructionManifest.WholeFile).OrderByDescending(artifact => artifact.IsPreferred)
            .ThenBy(artifact => artifact.RepresentationId).ToArray();
        if (whole.Length == 0)
            return Complete("no-whole-file-source");
        if (!whole[0].HasMeasuredEvidence)
        {
            // Original inspection is proof-bound; it cannot bootstrap an unmeasured identity holding.
            // A verified import must publish that first measurement and stage fresh demand.
            return whole[0].Container is RomContainerFacts.Identity
                ? Complete("source-unverified")
                : new MediaReconciliationDirective.Unavailable("source-unverified");
        }
        if (LogicalMediaInspectionPolicy.NeedsInspection(content))
        {
            var selected = whole[0];
            var decoder = selected.Encoding == RomRepresentationEncoding.Identity ? _media.OriginalInspectionDecoder : Decoder(selected.Encoding, null);
            if (decoder is null)
                return new MediaReconciliationDirective.Unavailable("decoder-unavailable");
            var source = LogicalInput(selected, content.OriginalFilename);
            var sources = new MediaReconciliationSources([source], null, [], MediaReconciliationReferenceFingerprint.Compute(content.References));
            var capture = Capture(MediaReconciliationOperation.Inspect, sources, decoder, null, null, null);
            return new MediaReconciliationDirective.Inspect(capture, source);
        }
        if (resolved.Unique is not { ManifestType: MediaManifestType.WholeFile } kind || !_policies.TryGetValue(kind.Kind, out var wholePolicy))
            return Complete(resolved.Outcome == MediaKindOutcome.Ambiguous ? "kind-ambiguous" : "kind-ineligible");
        var selectedSource = whole[0];
        var inputs = new List<MediaReconciliationInput> { LogicalInput(selectedSource, content.OriginalFilename) };
        inputs.AddRange(whole.Where(artifact => artifact.RepresentationId != selectedSource.RepresentationId).Select(artifact => (MediaReconciliationInput)new MediaReconciliationInput.Artifact(artifact.Facts, null, null)));
        var currentSources = new MediaReconciliationSources(inputs, null, content.Inspections.Select(row => row.Id).ToArray(), MediaReconciliationReferenceFingerprint.Compute(content.References));
        bool eligible = wholePolicy.IsEncodingEligible(content, ct);
        return Decide(wholePolicy, new(whole.Select(artifact => artifact.Facts).DistinctBy(graph => graph.Id).ToArray(), content.RepresentationId, eligible, ownershipNeedsAlignment: false), currentSources, content);

        MediaReconciliationDirective Verify(RomRepresentationFacts graph, RomDecoderIdentity decoder, MediaReconciliationSources sources, string? kindId)
        {
            var capture = Capture(MediaReconciliationOperation.Verify, sources, decoder, kindId, null, null);
            return new MediaReconciliationDirective.Verify(capture, new(graph.Id, graph.FileId, graph.ArtifactSha256, graph.ArtifactSize, decoder));
        }
        MediaReconciliationCapture Capture(MediaReconciliationOperation operation, MediaReconciliationSources sources, RomDecoderIdentity decoder, string? kindId,
            MediaReconciliationPolicy? policy, MediaReconciliationEncoder? encoder) => new(jobId, demand.Version, sources, operation, policy?.Recipe,
                CapabilityFingerprint(kindId), _media.LimitsFingerprint, decoder, kindId, encoder?.Build, encoder?.WholeDiscKind);
        MediaReconciliationDirective Decide(MediaReconciliationPolicy policy, MediaReconciliationState actual, MediaReconciliationSources sources, RomContentDescriptor? singleton)
        {
            switch (MediaReconciliationPlanner.Decide(policy, actual, demand.ConversionRequested, _capabilities))
            {
                case MediaReconciliationPlan.Verify verify:
                    var graph = actual.Representations.Single(graph => graph.Id == verify.RepresentationId);
                    var verificationSources = new MediaReconciliationSources([new MediaReconciliationInput.Artifact(graph, null, null)], sources.Layout,
                        singleton?.Inspections.Select(row => row.Id).ToArray() ?? [], sources.ReferenceFingerprint);
                    return Verify(graph, verify.Decoder, verificationSources, policy.Kind.Kind);
                case MediaReconciliationPlan.Release release:
                    var decoder = release.Decoder;
                    var releaseSources = IncludeHoldings(sources, actual.Representations, release.CanonicalRepresentationId, release.VerificationId);
                    return new MediaReconciliationDirective.Release(Capture(MediaReconciliationOperation.Release, releaseSources, decoder, policy.Kind.Kind, policy, null), release.CanonicalRepresentationId, release.VerificationId);
                case MediaReconciliationPlan.Encode:
                    // An older structural observation can classify the disc, but a new encoding must first
                    // retain the current raw decoder's measurement even if the subsequent encoder fails.
                    if (singleton is not null && sources.Inputs.OfType<MediaReconciliationInput.Logical>().Single() is { Graph.Container: RomContainerFacts.Identity } original &&
                        !original.Graph.Verifications.Any(proof => proof.Decoder.Equals(_media.OriginalInspectionDecoder)))
                        return new MediaReconciliationDirective.Inspect(Capture(MediaReconciliationOperation.Inspect,
                            new([original], null, sources.LogicalInspectionIds, sources.ReferenceFingerprint), _media.OriginalInspectionDecoder, null, null, null), original);
                    var resident = Decoder(policy.Kind.StoredEncoding, policy.Kind.Kind);
                    var encoder = _media.Encoder(policy.Kind.Kind);
                    if (resident is null || encoder is null)
                        return new MediaReconciliationDirective.Unavailable("encoder-unavailable");
                    return new MediaReconciliationDirective.Encode(Capture(MediaReconciliationOperation.Encode, IncludeHoldings(sources, actual.Representations, null, null), resident, policy.Kind.Kind, policy, encoder));
                case MediaReconciliationPlan.Unavailable unavailable:
                    return new MediaReconciliationDirective.Unavailable(unavailable.Reason.ToString());
                case MediaReconciliationPlan.None none:
                    return Complete(none.Reason.ToString());
                default:
                    throw new InvalidOperationException("Unsupported reconciliation plan.");
            }
        }
    }
    public bool AcceptsEncoding(MediaReconciliationCapture capture, MediaReconciliationEncoded encoded)
    {
        if (capture.KindId is not { } kind || !_policies.TryGetValue(kind, out var policy) || policy.Recipe != encoded.Recipe ||
            policy.Kind.StoredEncoding != encoded.Measured.Container.Encoding || !policy.MatchesParameters(encoded.Measured.Container) ||
            (policy.Kind.ManifestType == MediaManifestType.CdTracks) != (encoded.Measured.Manifest is RomReconstructionManifest.CdTracks))
            return false;
        if (encoded is MediaReconciliationEncoded.Whole whole)
        {
            var source = capture.Sources.Inputs.OfType<MediaReconciliationInput.Logical>().SingleOrDefault();
            if (source is null || !whole.SourceMeasurement.Representation.Measurement.Decoder.Equals(
                source.Graph.Container is RomContainerFacts.Identity ? _media.OriginalInspectionDecoder : capture.Decoder))
                return false;
        }
        return true;
    }
    public bool AcceptsAdoption(MediaReconciliationCapture capture, MediaReconciliationAdopted adopted)
    {
        var retained = capture.Sources.Inputs.OfType<MediaReconciliationInput.Artifact>().SingleOrDefault(input => input.CatalogWitness is not null)?.Graph;
        return capture.Identity.Operation == MediaReconciliationOperation.Adopt && capture.KindId is { } kind &&
            _policies.TryGetValue(kind, out var policy) && policy.Kind.ManifestType == MediaManifestType.CdTracks && policy.Recipe == capture.Identity.Recipe &&
            retained is not null && adopted.Measured.Manifest is RomReconstructionManifest.CdTracks &&
            adopted.Measured.Measurement.CheckedProfile == policy.AdoptionProfile && adopted.Measured.Measurement.Decoder.Equals(capture.Decoder) &&
            adopted.Measured.Measurement.ArtifactSha256 == retained.ArtifactSha256 && adopted.Measured.Measurement.ArtifactSize == retained.ArtifactSize &&
            adopted.Measured.Container.Fingerprint == retained.Container.Fingerprint && policy.MatchesParameters(adopted.Measured.Container) &&
            retained.CdBootInspections.All(observation => observation.Boot == adopted.Measurement.Boot);
    }
    private static MediaReconciliationSources IncludeHoldings(MediaReconciliationSources source, IReadOnlyList<RomRepresentationFacts> holdings, int? canonical, Guid? proof)
    {
        var inputs = source.Inputs.Select(input => input switch
        {
            MediaReconciliationInput.Logical logical when logical.Graph.Id == canonical => (MediaReconciliationInput)new MediaReconciliationInput.Logical(logical.Role, logical.Path!, logical.Graph, logical.Output.Id, proof!.Value),
            MediaReconciliationInput.Artifact artifact when artifact.Graph.Id == canonical => new MediaReconciliationInput.Artifact(artifact.Graph, proof, null),
            _ => input
        }).ToList();
        var held = inputs.Select(input => input switch { MediaReconciliationInput.Logical logical => logical.Graph.Id, MediaReconciliationInput.Artifact artifact => artifact.Graph.Id, _ => 0 }).ToHashSet();
        foreach (var graph in holdings.Where(graph => !held.Contains(graph.Id)))
            inputs.Add(new MediaReconciliationInput.Artifact(graph, graph.Id == canonical ? proof : null, null));
        return new(inputs, source.Layout, source.LogicalInspectionIds, source.ReferenceFingerprint);
    }
    private static MediaReconciliationDirective.Settle Complete(string code) => new(MediaReconciliationOutcomeKind.Completed, code);
    private RomDecoderIdentity? Decoder(RomRepresentationEncoding encoding, string? kind)
    {
        if (encoding == RomRepresentationEncoding.Identity)
            return _media.OriginalInspectionDecoder;
        if (kind is not null && !_capabilities.CanDecode(kind))
            return null;
        if (encoding == RomRepresentationEncoding.Chd)
            return _capabilities.Decoder(encoding) is { } build && (kind is null || _capabilities.Qualifies(kind, build.Rid)) ? new RomDecoderIdentity.Native(build) : null;
        string managed = _capabilities.ManagedDecoderBuild;
        return string.IsNullOrWhiteSpace(managed) ? null : new RomDecoderIdentity.Managed(managed);
    }
    private static MediaReconciliationInput.Logical LogicalInput(RomContentArtifact artifact, string filename)
    {
        var proof = artifact.Facts.Verifications.OrderBy(proof => proof.ObservedAt).ThenBy(proof => proof.Id).First();
        return new(MediaReconciliationSourceRole.Whole, filename, artifact.Facts, artifact.OutputId, proof.Id);
    }
    private Sha256 CapabilityFingerprint(string? kind)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("media-reconciliation-host/v1");
            writer.Write(_capabilities.ManagedDecoderBuild);
            var native = _capabilities.Decoder(RomRepresentationEncoding.Chd);
            writer.Write(native is not null);
            if (native is not null)
            { writer.Write(native.Rid); writer.Write(native.UpstreamCommit.AsSpan()); writer.Write(native.RecipeVersion); }
            writer.Write(kind is not null);
            if (kind is not null)
            { writer.Write(kind); writer.Write(_capabilities.CanDecode(kind)); writer.Write(_capabilities.CanEncode(kind)); writer.Write(_capabilities.QualifiedRuntime(kind) ?? ""); }
        }
        return Sha256.FromSpan(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
    }
}
