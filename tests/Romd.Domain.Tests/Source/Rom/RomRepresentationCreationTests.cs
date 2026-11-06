using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Source.Rom;

public sealed class RomRepresentationCreationTests
{
    private static readonly Guid Measurement = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SourceMeasurement = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static Sha256 Hash(char value) => Sha256.Parse(new string(value, 64));
    private static readonly RomContainerFacts.Rvz Container = new(0x30000, 1, 5, 19, 131072, false, 100);
    private static RomCreationSource Source(int target = 1) => new(target, 30, 40, SourceMeasurement, 50, Hash('b'), 200);
    private static RomRepresentationCreation Creation(string? mutation = null)
    {
        var encoder = mutation == "native" ? new RomChdEncoderBuild("linux-arm64", "mame0280", Hash('e'), 2) : null;
        return new(Guid.NewGuid(), mutation == "missing-proof" ? Guid.NewGuid() : Measurement, RecipeId.Parse("recipe/v3"),
            encoder?.BuildLabel ?? "encoder/actual", DateTimeOffset.UnixEpoch.AddTicks(123),
            mutation == "parameters" ? Hash('f') : Container.Fingerprint!.Value, encoder, [Source(mutation == "output" ? 2 : 1)]);
    }
    private static RomRepresentationFacts Facts(IReadOnlyList<RomRepresentationCreation> creations, bool identity = false)
    {
        var logical = new RomLogicalIdentity(100, Hash('a'), Sha1.Parse(new string('a', 40)), Md5.Parse(new string('a', 32)), Crc32.FromUInt32(0));
        RomContainerFacts container = identity ? RomContainerFacts.Identity.Instance : Container;
        var artifact = Hash(identity ? 'a' : 'c');
        long size = identity ? 100 : 512;
        var proof = new RomRepresentationVerification(Measurement, artifact, size, new RomDecoderIdentity.Managed("reader"), "checked/v1", "verifier",
            DateTimeOffset.UnixEpoch, [new(RomOutputSelector.Whole.Instance, logical)], container);
        return new(1, 2, artifact, size, container, RomReconstructionManifest.WholeFile.Instance,
            [new(1, 1, RomOutputSelector.Whole.Instance, logical)], [proof], [], creations);
    }

    [Fact]
    public void Creation_CompleteMeasuredGraph_CapturesEveryHistoricalSourceAndEncoderFact()
    {
        var source = Source();
        RomCreationSource[] sources = [source];
        var creation = new RomRepresentationCreation(Guid.NewGuid(), Measurement, RecipeId.Parse("recipe/v3"), "encoder/actual",
            DateTimeOffset.UnixEpoch.AddTicks(123), Container.Fingerprint!.Value, null, sources);
        sources[0] = Source(2);
        var facts = Facts([creation]);
        facts.Creations.Single().ShouldBeSameAs(creation);
        creation.Sources.Single().ShouldBe(source);
        creation.ComparedAt.UtcTicks.ShouldBe(DateTimeOffset.UnixEpoch.UtcTicks + 123);
        creation.Sources.Single().ArtifactSha256.ShouldBe(Hash('b'));
        creation.Sources.Single().VerificationId.ShouldBe(SourceMeasurement);
    }

    [Theory]
    [InlineData("missing-proof")]
    [InlineData("parameters")]
    [InlineData("output")]
    [InlineData("native")]
    [InlineData("identity")]
    [InlineData("duplicate")]
    public void Graph_CreationDoesNotDescribeExactMeasuredEncodedOutputs_Rejects(string mutation)
    {
        var creation = Creation(mutation);
        Should.Throw<ArgumentException>(() => Facts(mutation == "duplicate" ? [creation, creation] : [creation], mutation == "identity"));
    }

    [Theory]
    [InlineData("no-source")]
    [InlineData("duplicate-output")]
    [InlineData("native-label")]
    [InlineData("empty-parameters")]
    public void Creation_IncompleteOrContradictoryProvenance_Rejects(string mutation)
    {
        var native = mutation == "native-label" ? new RomChdEncoderBuild("linux-arm64", "mame0280", Hash('e'), 2) : null;
        RomCreationSource[] sources = mutation switch
        {
            "no-source" => [],
            "duplicate-output" => [Source(), Source()],
            _ => [Source()]
        };
        Should.Throw<ArgumentException>(() => new RomRepresentationCreation(Guid.NewGuid(), Measurement, RecipeId.Parse("recipe/v1"), "encoder/actual",
            DateTimeOffset.UnixEpoch, mutation == "empty-parameters" ? default : Container.Fingerprint!.Value, native, sources));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("old-comparison")]
    [InlineData("other-graph")]
    [InlineData("other-output")]
    [InlineData("other-artifact")]
    public void Graph_FreshByteIdenticalCreation_ReusesProofOnlyForTheExactMeasuredGraph(string change)
    {
        var source = new RomCreationSource(1, change == "other-graph" ? 30 : 1, change == "other-output" ? 40 : 1, Measurement, 2,
            Hash(change == "other-artifact" ? 'b' : 'c'), 512);
        var creation = new RomRepresentationCreation(Guid.NewGuid(), Measurement, RecipeId.Parse("recipe/v4"), "actual-encoder",
            change == "old-comparison" ? DateTimeOffset.UnixEpoch : DateTimeOffset.UnixEpoch.AddTicks(123), Container.Fingerprint!.Value, null, [source]);
        if (change == "valid")
            Facts([creation]).Creations.Single().ShouldBeSameAs(creation);
        else
            Should.Throw<ArgumentException>(() => Facts([creation]));
    }

    [Theory]
    [InlineData(0, 1, 1, 1, 0)]
    [InlineData(1, 0, 1, 1, 0)]
    [InlineData(1, 1, 0, 1, 0)]
    [InlineData(1, 1, 1, 0, 0)]
    [InlineData(1, 1, 1, 1, -1)]
    public void Source_IncompleteHistoricalWitness_Rejects(int target, int representation, int output, int file, long size) =>
        Should.Throw<ArgumentException>(() => new RomCreationSource(target, representation, output, SourceMeasurement, file, Hash('b'), size));
}
