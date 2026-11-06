using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Content;

public sealed class MediaReconciliationSourcesTests
{
    [Fact]
    public void Fingerprint_MultiInputDisc_CoversEveryExactInputRegardlessOfEnumerationOrder()
    {
        var first = Original(1);
        var second = Original(2);
        var cue = Cue(3, 'c');
        var inputs = new MediaReconciliationInput[] { Logical("first.bin", first), Logical("second.bin", second), cue };
        var captured = new MediaReconciliationSources(inputs, Layout(1), [], null);
        var reversed = new MediaReconciliationSources(inputs.Reverse().ToArray(), captured.Layout, [], null);
        reversed.Fingerprint.ShouldBe(captured.Fingerprint);
        var changed = new MediaReconciliationSources([inputs[0], Logical("second.bin", Original(3)), cue], captured.Layout, [], null);
        changed.Fingerprint.ShouldNotBe(captured.Fingerprint);
        inputs[1] = Logical("second.bin", Original(4));
        captured.Fingerprint.ShouldBe(reversed.Fingerprint);
    }

    [Theory]
    [InlineData("descriptor")]
    [InlineData("path")]
    [InlineData("layout")]
    [InlineData("reference")]
    [InlineData("inspection")]
    public void Fingerprint_ExactSourceDecisionChanges_InvalidatesTheAttempt(string mutation)
    {
        var graph = Original(1);
        var bin = Logical("data.bin", graph);
        var cue = Cue(3, 'c');
        var observation = Guid.NewGuid();
        var before = new MediaReconciliationSources([bin, cue], Layout(1), [observation], DeliveryFacts.Hash('d'));
        var after = new MediaReconciliationSources([
            mutation == "path" ? Logical("different.bin", graph) : bin,
            mutation == "descriptor" ? Cue(4, 'e') : cue], mutation == "layout" ? Layout(2) : before.Layout,
            mutation == "inspection" ? [Guid.NewGuid()] : [observation], mutation == "reference" ? DeliveryFacts.Hash('f') : before.ReferenceFingerprint);
        after.Fingerprint.ShouldNotBe(before.Fingerprint);
    }

    [Fact]
    public void Fingerprint_AdditionalHistoricalDecoderRow_DoesNotRewriteSourceIdentity()
    {
        var graph = Original(1);
        var original = new MediaReconciliationInput.Artifact(graph, null, null);
        var first = graph.Verifications.Single();
        var second = new RomRepresentationVerification(Guid.NewGuid(), graph.ArtifactSha256, graph.ArtifactSize,
            new RomDecoderIdentity.Managed("other-host-build"), first.CheckedProfile, first.VerifierBuild, first.ObservedAt,
            first.Outputs, graph.Container);
        var extended = new RomRepresentationFacts(graph.Id, graph.FileId, graph.ArtifactSha256, graph.ArtifactSize, graph.Container,
            graph.Manifest, graph.Outputs, [first, second], graph.Descriptors);
        new MediaReconciliationSources([new MediaReconciliationInput.Artifact(extended, null, null)], null, [], null).Fingerprint
            .ShouldBe(new MediaReconciliationSources([original], null, [], null).Fingerprint);
    }

    [Fact]
    public void Fingerprint_SelectedSourceProofChanges_SeparatesComparedCreationWitnesses()
    {
        var graph = Original(1);
        var first = graph.Verifications.Single();
        var second = new RomRepresentationVerification(Guid.NewGuid(), graph.ArtifactSha256, graph.ArtifactSize,
            new RomDecoderIdentity.Managed("other-host-build"), first.CheckedProfile, first.VerifierBuild, first.ObservedAt,
            first.Outputs, graph.Container);
        graph = new(graph.Id, graph.FileId, graph.ArtifactSha256, graph.ArtifactSize, graph.Container, graph.Manifest,
            graph.Outputs, [first, second], graph.Descriptors);
        var before = new MediaReconciliationSources([new MediaReconciliationInput.Logical(MediaReconciliationSourceRole.Whole,
            "disc.iso", graph, graph.Outputs.Single().Id, first.Id)], null, [], null);
        var after = new MediaReconciliationSources([new MediaReconciliationInput.Logical(MediaReconciliationSourceRole.Whole,
            "disc.iso", graph, graph.Outputs.Single().Id, second.Id)], null, [], null);
        after.Fingerprint.ShouldNotBe(before.Fingerprint);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("operation")]
    [InlineData("recipe")]
    [InlineData("capability")]
    [InlineData("limits")]
    public void AttemptIdentity_AnyRelevantDimensionChanges_NeverReusesUnchangedDeterministicFailure(string mutation)
    {
        var before = new MediaReconciliationAttemptIdentity(MediaReconciliationOperation.Encode, RecipeId.Parse("recipe/v1"),
            DeliveryFacts.Hash('a'), DeliveryFacts.Hash('b'), DeliveryFacts.Hash('c'));
        var after = new MediaReconciliationAttemptIdentity(mutation == "operation" ? MediaReconciliationOperation.Verify : before.Operation,
            mutation == "recipe" ? RecipeId.Parse("recipe/v2") : before.Recipe,
            mutation == "source" ? DeliveryFacts.Hash('d') : before.SourceFingerprint,
            mutation == "capability" ? DeliveryFacts.Hash('e') : before.CapabilityFingerprint,
            mutation == "limits" ? DeliveryFacts.Hash('f') : before.LimitsFingerprint);
        after.Fingerprint.ShouldNotBe(before.Fingerprint);
    }

    [Fact]
    public void Fingerprint_RetainedArchiveLogicalOwnersWithdrawn_KeepsByteVerificationSourceStable()
    {
        var facts = DeliveryFacts.CdContent().Preferred.Facts;
        var withdrawn = new RomRepresentationFacts(facts.Id, facts.FileId, facts.ArtifactSha256, facts.ArtifactSize, facts.Container,
            facts.Manifest, facts.Outputs.Select(output => new RomRepresentationOutput(output.Id, null, output.Selector, output.Identity)).ToArray(),
            facts.Verifications, facts.Descriptors);
        new MediaReconciliationSources([new MediaReconciliationInput.Artifact(withdrawn, null, null)], null, [], null).Fingerprint
            .ShouldBe(new MediaReconciliationSources([new MediaReconciliationInput.Artifact(facts, null, null)], null, [], null).Fingerprint);
    }

    [Fact]
    public void Fingerprint_RetainedDescriptorCustodyChanges_InvalidatesTheCompleteArchiveSource()
    {
        var facts = DeliveryFacts.CdContent().Preferred.Facts;
        var descriptor = facts.Descriptors.Single();
        var changed = new RomRepresentationFacts(facts.Id, facts.FileId, facts.ArtifactSha256, facts.ArtifactSize, facts.Container,
            facts.Manifest, facts.Outputs, facts.Verifications, [new(descriptor.Ordinal, descriptor.FileId + 1, descriptor.Sha256, descriptor.Size)]);
        new MediaReconciliationSources([new MediaReconciliationInput.Artifact(changed, null, null)], null, [], null).Fingerprint
            .ShouldNotBe(new MediaReconciliationSources([new MediaReconciliationInput.Artifact(facts, null, null)], null, [], null).Fingerprint);
    }

    [Fact]
    public void Fingerprint_ObservedContainerParametersChange_DoesNotReuseAnEarlierFailure()
    {
        var facts = DeliveryFacts.Rvz(1, 2, 3, 4, DeliveryFacts.Identity(8192), DeliveryFacts.Hash('b'), 4096, 1, true, null, null).Facts;
        var old = (RomContainerFacts.Rvz)facts.Container;
        var parameters = new RomContainerFacts.Rvz(old.Version, old.DiscType, old.Compression, old.CompressionLevel - 1,
            old.BlockBytes, old.HasWiiPartitions, old.LogicalBytes);
        var proof = facts.Verifications.Single();
        var measured = new RomRepresentationVerification(proof.Id, proof.ArtifactSha256, proof.ArtifactSize, proof.Decoder,
            proof.CheckedProfile, proof.VerifierBuild, proof.ObservedAt, proof.Outputs, parameters);
        var changed = new RomRepresentationFacts(facts.Id, facts.FileId, facts.ArtifactSha256, facts.ArtifactSize, parameters,
            facts.Manifest, facts.Outputs, [measured], []);
        new MediaReconciliationSources([new MediaReconciliationInput.Artifact(changed, null, null)], null, [], null).Fingerprint
            .ShouldNotBe(new MediaReconciliationSources([new MediaReconciliationInput.Artifact(facts, null, null)], null, [], null).Fingerprint);
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("revision")]
    [InlineData("catalog-file")]
    [InlineData("requirement")]
    [InlineData("generator")]
    public void Fingerprint_AdoptionCatalogWitnessChanges_InvalidatesExactSource(string mutation)
    {
        var graph = DeliveryFacts.CdContent().Preferred.Facts;
        var cue = ((RomReconstructionManifest.CdTracks)graph.Manifest).Members.OfType<RomCdStoredMember>().Single();
        var revision = Guid.NewGuid();
        MediaReconciliationLayout.Catalog Witness(bool changed) => new(changed && mutation == "binding" ? 8 : 7,
            changed && mutation == "revision" ? Guid.NewGuid() : revision, 1,
            new(changed && mutation == "catalog-file" ? 14 : 13, 1, "catalog-fingerprint", "cue-requirement", cue.Path,
                cue.Identity.Size + (changed && mutation == "requirement" ? 1 : 0), cue.Identity.Sha1, cue.Identity.Md5, cue.Identity.Crc32),
            changed && mutation == "generator" ? "new-measured-formatter" : "measured-formatter");
        var before = new MediaReconciliationSources([new MediaReconciliationInput.Artifact(graph, null, Witness(false))], Layout(1), [], null);
        var after = new MediaReconciliationSources([new MediaReconciliationInput.Artifact(graph, null, Witness(true))], before.Layout, [], null);
        after.Fingerprint.ShouldNotBe(before.Fingerprint);
    }

    [Fact]
    public void AttemptIdentity_CanonicalAdoptionWithoutRecipe_RefusesIncompletePolicyCapture() =>
        Should.Throw<ArgumentException>(() => new MediaReconciliationAttemptIdentity(MediaReconciliationOperation.Adopt, null,
            DeliveryFacts.Hash('a'), DeliveryFacts.Hash('b'), DeliveryFacts.Hash('c')));

    private static RomRepresentationFacts Original(int id) => DeliveryFacts.Original(id, id, id, id * 10, DeliveryFacts.Identity(2352), true).Facts;
    private static MediaReconciliationInput.Logical Logical(string path, RomRepresentationFacts facts) => new(MediaReconciliationSourceRole.Bin,
        path, facts, facts.Outputs.Single().Id, facts.Verifications.Single().Id);
    private static MediaReconciliationInput.Stored Cue(int id, char hash) => new(MediaReconciliationSourceRole.Cue, "disc.cue", id,
        new(98, DeliveryFacts.Hash(hash), DeliveryFacts.Short(hash), DeliveryFacts.Md5Value(hash), Crc32.FromUInt32(2)));
    private static MediaReconciliationLayout Layout(int releaseId) => new MediaReconciliationLayout.Original(1, Guid.Parse("5fc68732-654d-461a-b4b9-c41144fa22be"), 1, releaseId, "catalog-fingerprint", "disc.cue");
}
