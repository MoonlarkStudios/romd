using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Source.Rom;

public sealed class RomCdObservationFactsTests
{
    private static Sha256 Hash(char value) => Sha256.Parse(new string(value, 64));
    private static Sha1 Short(char value) => Sha1.Parse(new string(value, 40));
    private static RomCdFileIdentity Identity(long bytes, char value) => new(bytes, Hash(value), Short(value), Md5.Parse(new string(value, 32)), Crc32.FromUInt32(0));
    private static RomPs1CdBoot Boot() => new(RomPs1CdBoot.CurrentProfile, 0, 64, 24, 864, "SLUS_123.45", 26, 4096, 0x80010000, 0x80010000, 2048);
    private static RomRepresentationFacts Facts(string? mutation = null)
    {
        var logical = Identity(240 * 2352, 'a');
        var cue = Identity(10, 'c');
        var layout = new RomReconstructionManifest.CdTracks(240, [new(1, 0, 120, mutation == "audio-first" ? RomCdTrackKind.Audio : RomCdTrackKind.Mode2Raw), new(2, 120, 120, RomCdTrackKind.Audio)],
            [new RomCdBinMember("disc.bin", logical, logical, [1, 2]), new RomCdStoredMember("disc.cue", cue, RomCdStoredRole.Cue)]);
        var container = new RomContainerFacts.Chd(5, 240 * 2448, 4 * 2448, 2448, 60, [0, 0, 0, 0], false, Short('e'), Short('f'), [new(0x43485432, 1, "metadata"u8)]);
        var identity = new RomLogicalIdentity(logical.Size, logical.Sha256, logical.Sha1, logical.Md5, logical.Crc32);
        RomRepresentationVerification Proof(char pin) => new(Guid.NewGuid(), Hash('d'), 512, new RomDecoderIdentity.Native(new("linux-arm64", Short(pin), 2)),
            "complete/v1", "verifier", DateTimeOffset.UnixEpoch, [new(new RomOutputSelector.Member(0), identity)], container, Short('e'), Short('f'));
        var first = Proof('e');
        var second = Proof('f');
        var boot = new RomCdBootInspection(Guid.NewGuid(), mutation == "missing-proof" ? Guid.NewGuid() : first.Id, "inspector", DateTimeOffset.UnixEpoch.AddTicks(123), Boot());
        RomCdBootInspection[] boots = mutation switch
        {
            "duplicate-inspector" => [boot, new(Guid.NewGuid(), second.Id, "inspector", DateTimeOffset.UnixEpoch, Boot())],
            "new-inspector" => [boot, new(Guid.NewGuid(), second.Id, "inspector-new", DateTimeOffset.UnixEpoch, Boot())],
            _ => [boot]
        };
        var encoder = new RomChdEncoderBuild("linux-arm64", "mame0280", Hash('e'), 1);
        var creation = new RomRepresentationCreation(Guid.NewGuid(), second.Id, RecipeId.Parse("recipe/v1"), encoder.BuildLabel, DateTimeOffset.UnixEpoch,
            container.Fingerprint!.Value, encoder, [new(1, 30, 40, Guid.NewGuid(), 50, Hash('a'), logical.Size)],
            mutation == "missing-extension" ? null : new(mutation == "missing-boot" ? Guid.NewGuid() : boot.Id, Hash('b'), 100));
        return new(1, 2, Hash('d'), 512, container, layout, [new(1, 1, new RomOutputSelector.Member(0), identity)], [first, second], [new(1, 3, cue.Sha256, cue.Size)],
            mutation == "verify-only" ? [] : [creation], boots);
    }

    [Fact]
    public void Graph_VerifyOnlyBoot_DoesNotInventEncoderProvenance()
    {
        var facts = Facts("verify-only");
        facts.CdBootInspections.Count.ShouldBe(1);
        facts.Creations.ShouldBeEmpty();
    }

    [Fact]
    public void Graph_NewNativeProof_ReusesExactRetainedBootWitness()
    {
        var facts = Facts();
        facts.Verifications.Count.ShouldBe(2);
        var boot = facts.CdBootInspections.Single();
        var creation = facts.Creations.Single();
        creation.VerificationId.ShouldNotBe(boot.VerificationId);
        creation.Cd!.BootInspectionId.ShouldBe(boot.Id);
        creation.Cd.SourceRequestBytes.ShouldBe(100);
        boot.ObservedAt.ShouldBe(DateTimeOffset.UnixEpoch.AddTicks(123));
    }

    [Fact]
    public void Graph_NewManagedInspector_RetainsBothRawObservations() => Facts("new-inspector").CdBootInspections.Count.ShouldBe(2);

    [Theory]
    [InlineData("missing-proof")]
    [InlineData("audio-first")]
    [InlineData("duplicate-inspector")]
    [InlineData("missing-extension")]
    [InlineData("missing-boot")]
    public void Graph_IncompleteOrContradictoryCdObservation_Rejects(string mutation) => Should.Throw<ArgumentException>(() => Facts(mutation));

    [Theory]
    [InlineData(0)]
    [InlineData(524289)]
    public void Creation_UnboundedRequest_Rejects(long bytes) =>
        Should.Throw<ArgumentException>(() => new RomCdCreationObservation(Guid.NewGuid(), Hash('a'), bytes));
}
