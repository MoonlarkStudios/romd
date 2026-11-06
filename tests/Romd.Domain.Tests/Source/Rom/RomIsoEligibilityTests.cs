using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Source.Rom;

public sealed class RomIsoEligibilityTests
{
    private static readonly Sha256 Content = Sha256.Parse(new string('a', 64));

    [Theory]
    [InlineData(RomIsoDiscKind.GameCube, 0, 0, 0)]
    [InlineData(RomIsoDiscKind.Wii, 2, 1, 1)]
    [InlineData(RomIsoDiscKind.Wii, 64, 0, 64)]
    public void Create_SupportedActualLayout_PreservesBoundedFactsAndUtc(RomIsoDiscKind kind, int count, int standard, int korean)
    {
        var time = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.FromHours(-5));
        var observed = Create(RomIsoEligibilityStatus.Supported, kind, count, standard, korean, time);

        observed.ContentSha256.ShouldBe(Content);
        observed.ContentSize.ShouldBe(1_048_576);
        observed.DiscKind.ShouldBe(kind);
        observed.WiiPartitionCount.ShouldBe(count);
        observed.StandardKeyPartitions.ShouldBe(standard);
        observed.KoreanKeyPartitions.ShouldBe(korean);
        observed.InspectedAt.Offset.ShouldBe(TimeSpan.Zero);
        observed.InspectedAt.ShouldBe(time.ToUniversalTime());
    }

    [Theory]
    [InlineData(RomIsoEligibilityStatus.Supported, null, 0, 0, 0)]
    [InlineData(RomIsoEligibilityStatus.Supported, RomIsoDiscKind.GameCube, 1, 1, 0)]
    [InlineData(RomIsoEligibilityStatus.Supported, RomIsoDiscKind.Wii, 0, 0, 0)]
    [InlineData(RomIsoEligibilityStatus.Supported, RomIsoDiscKind.Wii, 65, 65, 0)]
    [InlineData(RomIsoEligibilityStatus.Supported, RomIsoDiscKind.Wii, 2, 1, 0)]
    [InlineData(RomIsoEligibilityStatus.Supported, RomIsoDiscKind.Wii, 1, -1, 2)]
    [InlineData(RomIsoEligibilityStatus.Unsupported, RomIsoDiscKind.GameCube, 0, 0, 0)]
    [InlineData(RomIsoEligibilityStatus.Unsupported, null, 1, 1, 0)]
    public void Create_InconsistentObservation_RejectsInventedFacts(RomIsoEligibilityStatus status, RomIsoDiscKind? kind,
        int count, int standard, int korean) =>
        Should.Throw<ArgumentException>(() => Create(status, kind, count, standard, korean));

    [Fact]
    public void Create_ContentUnsupported_HasNoInventedLayoutOrAvailability()
    {
        var observed = Create(RomIsoEligibilityStatus.Unsupported, null, 0, 0, 0);
        observed.Status.ShouldBe(RomIsoEligibilityStatus.Unsupported);
        observed.DiscKind.ShouldBeNull();
        observed.WiiPartitionCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("adapter", "nintendo\niso")]
    [InlineData("profile", "nintendo-iso/v1\0")]
    [InlineData("build", "inspector\u007f/mvid:test")]
    public void Create_ControlLabel_RejectsInvalidEvidenceInvariant(string field, string invalid) =>
        Should.Throw<ArgumentException>(() => new RomIsoEligibility(Content, 100,
            field == "adapter" ? invalid : "nintendo-iso", field == "profile" ? invalid : "nintendo-iso/v1",
            field == "build" ? invalid : "inspector/mvid:test", RomIsoEligibilityStatus.Supported,
            RomIsoDiscKind.GameCube, 0, 0, 0, DateTimeOffset.UtcNow));

    [Fact]
    public void Rehydrate_ExactCacheObservation_PreservesIdentityWithoutInventingNewTime()
    {
        var original = Create(RomIsoEligibilityStatus.Supported, RomIsoDiscKind.Wii, 2, 1, 1);
        var rehydrated = RomIsoEligibility.Rehydrate(original.Id, Content, original.ContentSize, original.Adapter,
            original.Profile, original.InspectorBuild, original.Status, original.DiscKind, 2, 1, 1, original.InspectedAt);
        rehydrated.ShouldBe(original);
        var repeated = Create(RomIsoEligibilityStatus.Supported, RomIsoDiscKind.Wii, 2, 1, 1);
        repeated.Id.ShouldNotBe(original.Id);
        repeated.MatchesObservation(original).ShouldBeTrue();
        Create(RomIsoEligibilityStatus.Unsupported, null, 0, 0, 0).MatchesObservation(original).ShouldBeFalse();
    }

    [Theory]
    [InlineData(RomDvdMediaKind.Dvd5)]
    [InlineData(RomDvdMediaKind.Dvd9)]
    public void Create_Ps2BootAndIndependentDvdReference_RequiresExactSeparateMediaEvidence(RomDvdMediaKind mediaKind)
    {
        var reference = new RomDvdMediaReference(ChdEvidenceSamples.ReferenceRecordId, ChdEvidenceSamples.Content, ChdEvidenceSamples.LogicalBytes,
            ChdEvidenceSamples.LogicalSha1, ChdEvidenceSamples.LogicalMd5, ChdEvidenceSamples.LogicalCrc,
            mediaKind, "published-dvd-reference/v1", ChdEvidenceSamples.OtherHash, "synthetic-independent-reference");
        var facts = new RomPs2DvdEligibilityFacts(reference.ContentSha256, reference.ContentSize, reference.Sha1,
            reference.Md5, reference.Crc32, ChdEvidenceSamples.Boot(), reference);
        var observation = Ps2(facts);

        observation.DiscKind.ShouldBe(RomIsoDiscKind.PlayStation2Dvd);
        observation.Ps2DvdFacts.ShouldBe(facts);
        observation.ContentSize.ShouldBe(ChdEvidenceSamples.LogicalBytes);
        observation.Ps2DvdFacts!.DvdReference.MediaKind.ShouldBe(mediaKind);
        observation.WiiPartitionCount.ShouldBe(0);
        observation.StandardKeyPartitions.ShouldBe(0);
        observation.KoreanKeyPartitions.ShouldBe(0);
        observation.Profile.ShouldNotBe(facts.Boot.Profile);
    }

    [Fact]
    public void Reference_EmptySelectedRecordIdentity_RejectsWithoutFallback()
    {
        Should.Throw<ArgumentException>(() => new RomDvdMediaReference(Guid.Empty, ChdEvidenceSamples.Content,
            ChdEvidenceSamples.LogicalBytes, ChdEvidenceSamples.LogicalSha1, ChdEvidenceSamples.LogicalMd5,
            ChdEvidenceSamples.LogicalCrc, RomDvdMediaKind.Dvd5, "published-dvd-reference/v1",
            ChdEvidenceSamples.OtherHash, "synthetic-reference"));
    }

    [Fact]
    public void MatchesObservation_SamePhysicalFactsDifferentSelectedRecord_IsDifferentImmutableEvidence()
    {
        var original = Ps2(ChdEvidenceSamples.Eligibility());
        var repeated = Ps2(ChdEvidenceSamples.Eligibility("record-id"));
        var originalFacts = original.Ps2DvdFacts.ShouldNotBeNull();
        var repeatedFacts = repeated.Ps2DvdFacts.ShouldNotBeNull();
        originalFacts.DvdReference.ReferenceRecordId.ShouldBe(ChdEvidenceSamples.ReferenceRecordId);
        repeatedFacts.DvdReference.ReferenceRecordId.ShouldNotBe(ChdEvidenceSamples.ReferenceRecordId);
        repeatedFacts.DvdReference.ReferenceSha256.ShouldBe(originalFacts.DvdReference.ReferenceSha256);
        repeated.MatchesObservation(original).ShouldBeFalse();
    }

    [Fact]
    public void Create_BootPositiveSmallIsoWithoutIndependentDvdReference_CannotGainDvdEligibility()
    {
        var boot = ChdEvidenceSamples.Boot();
        boot.LogicalBytes.ShouldBeLessThan(700L << 20);
        Should.Throw<ArgumentException>(() => Ps2(null));
        Should.Throw<ArgumentNullException>(() => ChdEvidenceSamples.Eligibility("missing-reference"));
        Should.Throw<ArgumentNullException>(() => ChdEvidenceSamples.Eligibility("missing-boot"));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("length")]
    [InlineData("sha1")]
    [InlineData("md5")]
    [InlineData("crc")]
    public void Create_ForeignPublishedReference_RejectsAnyLogicalIdentityMismatch(string changed) =>
        Should.Throw<ArgumentException>(() => ChdEvidenceSamples.Eligibility(changed));

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void Reference_UnknownOrCdMediaKind_CannotSatisfyDvdEvidence(int mediaKind) =>
        Should.Throw<ArgumentException>(() => new RomDvdMediaReference(ChdEvidenceSamples.ReferenceRecordId, ChdEvidenceSamples.Content, ChdEvidenceSamples.LogicalBytes,
            ChdEvidenceSamples.LogicalSha1, ChdEvidenceSamples.LogicalMd5, ChdEvidenceSamples.LogicalCrc,
            (RomDvdMediaKind)mediaKind, "published-dvd-reference/v1", ChdEvidenceSamples.OtherHash, "synthetic-reference"));

    [Theory]
    [InlineData("content")]
    [InlineData("length")]
    [InlineData("profile")]
    [InlineData("build")]
    [InlineData("nintendo-count")]
    [InlineData("nintendo-kind")]
    [InlineData("unsupported")]
    public void Create_Ps2FactsCannotAttachToForeignGenericObservation(string changed) =>
        Should.Throw<ArgumentException>(() => Ps2(ChdEvidenceSamples.Eligibility(), changed));

    [Fact]
    public void Rehydrate_Ps2FactsAndIndependentReference_PreservesExactOriginalObservation()
    {
        var original = Ps2(ChdEvidenceSamples.Eligibility());
        var rehydrated = RomIsoEligibility.Rehydrate(original.Id, original.ContentSha256, original.ContentSize,
            original.Adapter, original.Profile, original.InspectorBuild, original.Status, original.DiscKind,
            0, 0, 0, original.InspectedAt, original.Ps2DvdFacts);
        rehydrated.ShouldBe(original);
        var repeated = Ps2(ChdEvidenceSamples.Eligibility());
        repeated.Id.ShouldNotBe(original.Id);
        repeated.MatchesObservation(original).ShouldBeTrue();
        Ps2(ChdEvidenceSamples.Eligibility("dvd9")).MatchesObservation(original).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => RomIsoEligibility.Rehydrate(original.Id, original.ContentSha256, original.ContentSize,
            original.Adapter, original.Profile, original.InspectorBuild, original.Status, original.DiscKind, 0, 0, 0, original.InspectedAt));
    }

    [Theory]
    [InlineData(864U)]
    [InlineData(167U)]
    public void BootFacts_ShortDeclaredRoot_PreservesBytesForCompletePs2Observation(uint rootDirectoryBytes)
    {
        var boot = new RomPs2BootLayoutFacts(ChdEvidenceSamples.LogicalBytes, 64, 16, 24, rootDirectoryBytes,
            25, 60, 26, 512, "cdrom0:\\SLUS_203.12;1", "cdrom0:\\SLUS_203.12;1", "SLUS_203.12",
            0x00100000, 1, RomPs2BootLayoutFacts.ProfileVersion, "ps2-inspector/mvid:synthetic");
        var reference = ChdEvidenceSamples.DvdReference();
        var facts = new RomPs2DvdEligibilityFacts(reference.ContentSha256, reference.ContentSize, reference.Sha1,
            reference.Md5, reference.Crc32, boot, reference);
        var observation = Ps2(facts);

        boot.RootDirectoryBytes.ShouldBe(rootDirectoryBytes);
        observation.Ps2DvdFacts.ShouldBe(facts);
        observation.Ps2DvdFacts!.Boot.ShouldBe(boot);
        observation.DiscKind.ShouldBe(RomIsoDiscKind.PlayStation2Dvd);
    }

    [Theory]
    [InlineData(24U, 0U, 25U)]
    [InlineData(24U, 262145U, 25U)]
    [InlineData(63U, 2049U, 25U)]
    [InlineData(24U, 2049U, 25U)]
    [InlineData(24U, 4097U, 27U)]
    public void BootFacts_ShortRootStillRequiresBoundedContainedDisjointExtents(uint rootBlock,
        uint rootBytes, uint cnfBlock) =>
        Should.Throw<ArgumentException>(() => new RomPs2BootLayoutFacts(ChdEvidenceSamples.LogicalBytes, 64, 16,
            rootBlock, rootBytes, cnfBlock, 60, 26, 512, "cdrom0:\\SLUS_203.12;1", "cdrom0:\\SLUS_203.12;1",
            "SLUS_203.12", 0x00100000, 1, RomPs2BootLayoutFacts.ProfileVersion, "ps2-inspector/mvid:synthetic"));

    [Theory]
    [InlineData("zero-volume")]
    [InlineData("root-range")]
    [InlineData("overlap")]
    [InlineData("root-limit")]
    [InlineData("cnf-limit")]
    [InlineData("elf-truncated")]
    [InlineData("entrypoint")]
    [InlineData("program-count")]
    [InlineData("nested-path")]
    [InlineData("raw-path")]
    [InlineData("profile")]
    [InlineData("build")]
    public void BootFacts_UnboundedOrContradictoryObservation_Rejects(string changed) =>
        Should.Throw<ArgumentException>(() => new RomPs2BootLayoutFacts(ChdEvidenceSamples.LogicalBytes,
            changed == "zero-volume" ? 0U : 64U, 16,
            changed == "root-range" ? uint.MaxValue : 24U, changed == "root-limit" ? (256U << 10) + 2048 : 2048U,
            changed == "overlap" ? 24U : 25U, changed == "cnf-limit" ? (16U << 10) + 1 : 60U,
            26, changed == "elf-truncated" ? 52U : 512U,
            changed == "raw-path" ? "cdrom0:\\SLUS_999.99;1" : "cdrom0:\\SLUS_203.12;1",
            changed == "nested-path" ? "cdrom0:\\DIR\\SLUS_203.12;1" : "cdrom0:\\SLUS_203.12;1", "SLUS_203.12",
            changed == "entrypoint" ? 0U : 0x00100000U, changed == "program-count" ? (ushort)129 : (ushort)1,
            changed == "profile" ? "unknown-boot/v1" : RomPs2BootLayoutFacts.ProfileVersion,
            changed == "build" ? "build\n" : "ps2-inspector/mvid:synthetic"));

    [Theory]
    [InlineData("root", false)]
    [InlineData("root", true)]
    [InlineData("cnf", false)]
    [InlineData("cnf", true)]
    [InlineData("program-headers", false)]
    [InlineData("program-headers", true)]
    public void BootFacts_IndependentCeiling_RejectsOnlyAboveExactBound(string field, bool exceeds)
    {
        RomPs2BootLayoutFacts Create() => new(512 * 2048L, 512, 16,
            24, field == "root" && exceeds ? (256U << 10) + 2048 : 256U << 10,
            160, field == "cnf" && exceeds ? (16U << 10) + 1 : 16U << 10,
            180, 8192, "cdrom0:\\SLUS_203.12;1", "cdrom0:\\SLUS_203.12;1", "SLUS_203.12",
            0x100000, field == "program-headers" && exceeds ? (ushort)129 : (ushort)128,
            RomPs2BootLayoutFacts.ProfileVersion, "ps2-inspector/mvid:synthetic");

        if (exceeds)
            Should.Throw<ArgumentException>(Create);
        else
            Create().LogicalBytes.ShouldBe(512 * 2048L);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("sha1")]
    [InlineData("md5")]
    [InlineData("crc")]
    [InlineData("reference")]
    [InlineData("profile")]
    [InlineData("source")]
    public void Reference_IncompleteHashesOrInvalidProvenance_Rejects(string changed) =>
        Should.Throw<ArgumentException>(() => new RomDvdMediaReference(ChdEvidenceSamples.ReferenceRecordId, changed == "content" ? default : ChdEvidenceSamples.Content,
            ChdEvidenceSamples.LogicalBytes, changed == "sha1" ? default : ChdEvidenceSamples.LogicalSha1,
            changed == "md5" ? default : ChdEvidenceSamples.LogicalMd5, changed == "crc" ? default : ChdEvidenceSamples.LogicalCrc,
            RomDvdMediaKind.Dvd5, changed == "profile" ? "reference\n" : "published-dvd-reference/v1",
            changed == "reference" ? default : ChdEvidenceSamples.OtherHash, changed == "source" ? new string('x', 257) : "synthetic-reference"));

    [Fact]
    public void Reference_PublishedZeroCrc_RemainsACompleteLogicalHash()
    {
        var zero = Crc32.FromUInt32(0);
        var reference = new RomDvdMediaReference(ChdEvidenceSamples.ReferenceRecordId, ChdEvidenceSamples.Content, ChdEvidenceSamples.LogicalBytes,
            ChdEvidenceSamples.LogicalSha1, ChdEvidenceSamples.LogicalMd5, zero, RomDvdMediaKind.Dvd5,
            "published-dvd-reference/v1", ChdEvidenceSamples.OtherHash, "synthetic-reference");
        new RomPs2DvdEligibilityFacts(reference.ContentSha256, reference.ContentSize, reference.Sha1,
            reference.Md5, zero, ChdEvidenceSamples.Boot(), reference).Crc32.ShouldBe(zero);
    }

    private static RomIsoEligibility Ps2(RomPs2DvdEligibilityFacts? facts, string? changed = null) =>
        new(changed == "content" ? ChdEvidenceSamples.OtherHash : ChdEvidenceSamples.Content,
            changed == "length" ? ChdEvidenceSamples.LogicalBytes + 2048 : ChdEvidenceSamples.LogicalBytes,
            "ps2-iso", changed == "profile" ? RomPs2BootLayoutFacts.ProfileVersion : RomPs2DvdEligibilityFacts.ProfileVersion,
            changed == "build" ? "other-inspector" : "ps2-inspector/mvid:synthetic",
            changed == "unsupported" ? RomIsoEligibilityStatus.Unsupported : RomIsoEligibilityStatus.Supported,
            changed == "unsupported" ? null : changed == "nintendo-kind" ? RomIsoDiscKind.GameCube : RomIsoDiscKind.PlayStation2Dvd,
            changed == "nintendo-count" ? 1 : 0, 0, 0, DateTimeOffset.UtcNow, facts);

    private static RomIsoEligibility Create(RomIsoEligibilityStatus status, RomIsoDiscKind? kind,
        int count, int standard, int korean, DateTimeOffset? time = null) =>
        new(Content, 1_048_576, "nintendo-iso", "nintendo-iso/v1", "iso-inspector/mvid:test", status, kind,
            count, standard, korean, time ?? DateTimeOffset.UtcNow);
}
