using Romd.Admin.Application.Hashing;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Rom;

public sealed class PreparedRomEvidenceTests
{
    private static readonly Sha256 Logical = Sha256.Parse(new string('a', 64));
    private static readonly Sha256 ArtifactHash = Sha256.Parse(new string('b', 64));
    private static readonly DatHashResult Hashes = new(Sha1.Parse(new string('c', 40)), Md5.Parse(new string('d', 32)), Crc32.FromUInt32(12), 100);

    [Fact]
    public void Create_ActualObservationAndIndependentComparison_BindsLogicalBytesAndExactMeasurement()
    {
        var verification = Output();
        var eligibility = Eligibility();
        RomSourceEquivalence.TryCreate(Source(), verification, "rvz-canonical/v1", "encoder/mvid:test",
            DateTimeOffset.UtcNow, out var comparison).ShouldBeTrue();

        var prepared = Prepare(verification, eligibility, comparison);

        prepared.Eligibility.ShouldBeSameAs(eligibility);
        prepared.SourceEquivalence.ShouldBeSameAs(comparison);
        prepared.Verification.Id.ShouldBe(comparison!.OutputVerificationId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Create_EligibilityForOtherLogicalBytes_RejectsMismatchedFact(bool changeHash)
    {
        var eligibility = new RomIsoEligibility(changeHash ? ArtifactHash : Logical, changeHash ? 100 : 101,
            "nintendo-iso", "nintendo-iso/v1", "inspector/mvid:test", RomIsoEligibilityStatus.Supported,
            RomIsoDiscKind.GameCube, 0, 0, 0, DateTimeOffset.UtcNow);

        Should.Throw<ArgumentException>(() => Prepare(Output(), eligibility, null));
    }

    [Fact]
    public void Create_ComparisonForOtherMeasurementOfSameArtifact_RejectsUnboundProof()
    {
        RomSourceEquivalence.TryCreate(Source(), Output(), "rvz-canonical/v1", "encoder/mvid:test",
            DateTimeOffset.UtcNow, out var comparison).ShouldBeTrue();

        Should.Throw<ArgumentException>(() => Prepare(Output(), null, comparison));
    }

    [Fact]
    public void Create_ImportedArtifactWithoutOptionalObservations_DoesNotInventEligibilityOrSourceComparison()
    {
        var prepared = Prepare(Output(), null, null);

        prepared.Eligibility.ShouldBeNull();
        prepared.SourceEquivalence.ShouldBeNull();
    }

    private static PreparedRom.WholeFile Prepare(RomVerification verification, RomIsoEligibility? eligibility, RomSourceEquivalence? comparison) =>
        new(new Artifact(), "disc.iso", Logical, Hashes, RomRepresentationEncoding.Rvz, verification, new RomContainerFacts.Rvz(0x01000000, 1, 5, 12, 131072, false, 100),
            eligibility: eligibility, sourceEquivalence: comparison);

    private static RomIsoEligibility Eligibility() => new(Logical, 100, "nintendo-iso", "nintendo-iso/v1",
        "inspector/mvid:test", RomIsoEligibilityStatus.Supported, RomIsoDiscKind.GameCube, 0, 0, 0, DateTimeOffset.UtcNow);

    private static RomVerification Source() => new(Logical, 100, RomRepresentationEncoding.Identity, Logical, 100,
        Hashes.Sha1, Hashes.Md5, Hashes.Crc32, "identity-output/v1", "hasher", "verifier", DateTimeOffset.UtcNow);

    private static RomVerification Output() => new(ArtifactHash, 20, RomRepresentationEncoding.Rvz, Logical, 100,
        Hashes.Sha1, Hashes.Md5, Hashes.Crc32, "rvz-disc-supervised/v1", "decoder", "verifier", DateTimeOffset.UtcNow);

    private sealed class Artifact : ITempFile
    {
        public string Path => "unused";
        public Sha256 FileSha256 => ArtifactHash;
        public long FileSize => 20;
        public Stream OpenRead() => throw new InvalidOperationException("Constructor validation does not read the artifact.");
        public bool TryBeginChildUse() => true;
        public bool TryEndChildUse() => true;
        public bool Quarantine() => true;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
