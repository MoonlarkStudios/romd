using System.Buffers.Binary;
using System.Text;
using Romd.Media.PlayStation;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests.PlayStation;

/// <summary>Synthetic ISO9660 records and MIPS headers, with borrowed logical views; no game bytes or native code.</summary>
public sealed class Ps2DvdIdentifierTests
{
    private const int Sector = 2048, Pvd = 16 * Sector, Root = 24 * Sector,
        Cnf = 25 * Sector, Elf = 26 * Sector, CnfRecord = Root + 68, BootRecord = CnfRecord + 46;
    private const string Boot2 = "BOOT2 = cdrom0:\\SLUS_203.12;1\r\nVER = 1.00\r\nVMODE = NTSC\r\n";

    [Fact]
    public async Task InspectAsync_RootBootMipsElf_ReturnsObservedFactsWithoutReadingProgramBodyOrDisposing()
    {
        var source = new ObservedContent(Image());
        var result = await Ps2DvdIdentifier.InspectAsync(source, 1L << 20);

        result.Status.ShouldBe(Ps2DvdIdentificationStatus.Identified);
        result.Facts.ShouldBe(new Ps2DvdObservedFacts(source.Length, 64, 16, 24, Sector, 25,
            (uint)Encoding.ASCII.GetByteCount(Boot2), 26, 512, "cdrom0:\\SLUS_203.12;1",
            "cdrom0:\\SLUS_203.12;1", "SLUS_203.12", 0x00100000, 1));
        source.MaximumReadEnd.ShouldBe(Elf + 84);
        source.RequestedBytes.ShouldBe(32 + 2 * Sector + Sector + Boot2.Length + 52 + 32);
        source.Disposed.ShouldBeFalse();
    }

    [Fact]
    public async Task InspectAsync_ProjectedLogicalBytes_MatchesRawIsoFacts()
    {
        byte[] logical = Image(), storage = new byte[logical.Length + 512];
        "MComprHD"u8.CopyTo(storage);
        logical.CopyTo(storage, 512);
        var backing = new ObservedContent(storage);
        var view = new Projection(backing, 512, logical.Length);

        var raw = await Ps2DvdIdentifier.InspectAsync(new ObservedContent(logical), 1L << 20);
        var projected = await Ps2DvdIdentifier.InspectAsync(view, 1L << 20);

        projected.ShouldBe(raw);
        projected.Status.ShouldBe(Ps2DvdIdentificationStatus.Identified);
        backing.MinimumReadOffset.ShouldBe(512);
        backing.Disposed.ShouldBeFalse();
    }

    [Fact]
    public async Task InspectAsync_LowercaseAndWhitespaceBoot2_NormalizesButPreservesObservedValue()
    {
        byte[] bytes = Image();
        Config(bytes, " \tboot2\t= CDROM0:\\slus_203.12;1 \nVER=1.00\n");
        var facts = (await Inspect(bytes)).Facts.ShouldNotBeNull();
        facts.BootPath.ShouldBe("cdrom0:\\SLUS_203.12;1");
        facts.BootSerial.ShouldBe("SLUS_203.12");
        facts.ObservedBoot2Value.ShouldBe("CDROM0:\\slus_203.12;1");
    }

    [Fact]
    public async Task InspectAsync_ShortPositionalReads_CompletesBoundedInspection()
    {
        var source = new ObservedContent(Image()) { MaximumReadBytes = 7 };
        (await Ps2DvdIdentifier.InspectAsync(source, 1L << 20)).Status.ShouldBe(Ps2DvdIdentificationStatus.Identified);
        source.Reads.ShouldBeGreaterThan(50);
        source.Disposed.ShouldBeFalse();
    }

    [Fact]
    public async Task InspectAsync_RootDirectorySectorPadding_FindsRecordsInSecondSector()
    {
        byte[] bytes = Image();
        byte[] records = bytes.AsSpan(CnfRecord, 92).ToArray();
        byte[] configuration = bytes.AsSpan(Cnf, Sector).ToArray(), executable = bytes.AsSpan(Elf, Sector).ToArray();
        bytes.AsSpan(CnfRecord, Sector - 68).Clear();
        Both32(bytes, Pvd + 156 + 10, 2 * Sector);
        Both32(bytes, Root + 10, 2 * Sector);
        Both32(bytes, Root + 34 + 10, 2 * Sector);
        // Move CNF/ELF data and point the second-sector records at their new extents.
        configuration.CopyTo(bytes, 28 * Sector);
        executable.CopyTo(bytes, 29 * Sector);
        bytes.AsSpan(Cnf, Sector).Clear();
        records.CopyTo(bytes, Root + Sector);
        Both32(bytes, Root + Sector + 2, 28);
        Both32(bytes, Root + Sector + 46 + 2, 29);
        (await Inspect(bytes)).Status.ShouldBe(Ps2DvdIdentificationStatus.Identified);
    }

    [Fact]
    public async Task InspectAsync_ExtentsBeyondFourGiB_UsesLogicalLongOffsetsWithoutFullRead()
    {
        byte[] bytes = Image();
        const uint additionalBlocks = 1U << 21;
        Both32(bytes, Pvd + 80, additionalBlocks + 64);
        Both32(bytes, Pvd + 156 + 2, additionalBlocks + 24);
        Both32(bytes, Root + 2, additionalBlocks + 24);
        Both32(bytes, Root + 34 + 2, additionalBlocks + 24);
        Both32(bytes, CnfRecord + 2, additionalBlocks + 25);
        Both32(bytes, BootRecord + 2, additionalBlocks + 26);
        var source = new RelocatedView(new ObservedContent(bytes));

        var result = await Ps2DvdIdentifier.InspectAsync(source, 10L << 30);

        result.Status.ShouldBe(Ps2DvdIdentificationStatus.Identified);
        result.Facts.ShouldNotBeNull().LogicalBytes.ShouldBe((1L << 32) + 64 * Sector);
        result.Facts!.BootExecutableBlock.ShouldBe(additionalBlocks + 26);
        source.MaximumReadEnd.ShouldBe((1L << 32) + Elf + 84);
    }

    [Fact]
    public async Task InspectAsync_CompatibilityRootDeclared864Bytes_RecognizesCompleteRecords()
    {
        byte[] bytes = Image();
        SetCompatibilityRoot864(bytes, 512);
        var source = new ObservedContent(bytes);

        var result = await Ps2DvdIdentifier.InspectAsync(source, source.Length);

        result.Status.ShouldBe(Ps2DvdIdentificationStatus.Identified);
        result.Facts.ShouldNotBeNull().RootDirectoryBytes.ShouldBe(864U);
        result.Facts!.BootSerial.ShouldBe("SLUS_203.12");
        source.MaximumReadEnd.ShouldBe(Elf + 84);
        source.RequestedBytes.ShouldBe(32 + 2 * Sector + 864 + Boot2.Length + 52 + 32);
        source.Disposed.ShouldBeFalse();
    }

    [Theory]
    [InlineData(160)]
    [InlineData(167)]
    public async Task InspectAsync_CompatibilityPartialRoot_UsesOnlyDeclaredBytes(uint count)
    {
        byte[] bytes = Image();
        SetCompactRootBytes(bytes, count);
        // Bytes outside the declared directory are not part of the root observation.
        bytes[Root + count] = 0xFF;
        var source = new ObservedContent(bytes);

        var result = await Ps2DvdIdentifier.InspectAsync(source, source.Length);

        result.Status.ShouldBe(Ps2DvdIdentificationStatus.Identified);
        result.Facts.ShouldNotBeNull().RootDirectoryBytes.ShouldBe(count);
        source.MaximumReadEnd.ShouldBe(Elf + 84);
        source.RequestedBytes.ShouldBe(32 + 2 * Sector + count + Boot2.Length + 52 + 32);
        source.Disposed.ShouldBeFalse();
    }

    [Theory]
    [InlineData("truncated-record")]
    [InlineData("partial-record-header")]
    [InlineData("nonzero-padding")]
    [InlineData("self-size-disagrees")]
    public async Task InspectAsync_CompatibilityPartialRootMalformedTail_ReturnsMalformed(string control)
    {
        byte[] bytes = Image();
        uint count = control switch { "truncated-record" => 159U, "partial-record-header" => 163U, "nonzero-padding" => 165U, _ => 160U };
        SetCompactRootBytes(bytes, count);
        switch (control)
        {
            case "partial-record-header":
                bytes[Root + 160] = 34;
                break;
            case "nonzero-padding":
                bytes[Root + 164] = 1;
                break;
            case "self-size-disagrees":
                Both32(bytes, Root + 10, count + 1);
                break;
        }
        var source = new ObservedContent(bytes);

        var result = await Ps2DvdIdentifier.InspectAsync(source, source.Length);

        result.Status.ShouldBe(Ps2DvdIdentificationStatus.Malformed);
        result.Facts.ShouldBeNull();
        source.RequestedBytes.ShouldBe(32 + 2 * Sector + count);
        source.MaximumReadEnd.ShouldBe(Root + count);
        source.Disposed.ShouldBeFalse();
    }

    [Fact]
    public async Task InspectAsync_CompatibilityEmptyLoadWithNoncongruentOffset_DoesNotSupplyEntry()
    {
        // Synthetic headers reproduce the measured empty PH fields, with no executable body or game bytes.
        var source = new ObservedContent(CompatibilityEmptyLoadImage()) { DeclaredLength = 2400L * Sector };

        var result = await Ps2DvdIdentifier.InspectAsync(source, source.Length);

        result.Status.ShouldBe(Ps2DvdIdentificationStatus.Identified);
        var facts = result.Facts.ShouldNotBeNull();
        facts.BootExecutableBytes.ShouldBe(4615960U);
        facts.EntryPoint.ShouldBe(0x00100008U);
        facts.ProgramHeaderCount.ShouldBe((ushort)2);
        source.MaximumReadEnd.ShouldBe(Elf + 116);
        source.RequestedBytes.ShouldBe(32 + 3 * Sector + Boot2.Length + 52 + 64);
        source.Disposed.ShouldBeFalse();
    }

    [Fact]
    public async Task InspectAsync_CompatibilityObservedDirectoryAndEmptyLoad_RecognizesBootLayout()
    {
        byte[] bytes = CompatibilityEmptyLoadImage();
        SetCompatibilityRoot864(bytes, 4615960);
        var source = new ObservedContent(bytes) { DeclaredLength = 2400L * Sector };

        var result = await Ps2DvdIdentifier.InspectAsync(source, source.Length);

        result.Status.ShouldBe(Ps2DvdIdentificationStatus.Identified);
        var facts = result.Facts.ShouldNotBeNull();
        facts.RootDirectoryBytes.ShouldBe(864U);
        facts.BootExecutableBytes.ShouldBe(4615960U);
        facts.EntryPoint.ShouldBe(0x00100008U);
        facts.ProgramHeaderCount.ShouldBe((ushort)2);
        source.MaximumReadEnd.ShouldBe(Elf + 116);
        source.RequestedBytes.ShouldBe(32 + 2 * Sector + 864 + Boot2.Length + 52 + 64);
        source.Disposed.ShouldBeFalse();
    }

    [Theory]
    [InlineData("file-without-memory")]
    [InlineData("memory-only-misaligned")]
    [InlineData("nonempty-misaligned")]
    [InlineData("empty-outside-file")]
    [InlineData("empty-unknown-flags")]
    [InlineData("empty-bad-alignment")]
    [InlineData("only-empty-loads")]
    [InlineData("entry-only-in-zero-fill")]
    public async Task InspectAsync_CompatibilityEmptyLoad_NearbyMalformedOrMissingEntry_Rejects(string control)
    {
        byte[] bytes = CompatibilityEmptyLoadImage();
        const int empty = Elf + 84;
        switch (control)
        {
            case "file-without-memory":
                U32(bytes, empty + 16, 1);
                break;
            case "memory-only-misaligned":
                U32(bytes, empty + 20, 1);
                break;
            case "nonempty-misaligned":
                U32(bytes, empty + 16, 1);
                U32(bytes, empty + 20, 1);
                break;
            case "empty-outside-file":
                U32(bytes, empty + 4, 4615961);
                break;
            case "empty-unknown-flags":
                U32(bytes, empty + 24, 8);
                break;
            case "empty-bad-alignment":
                U32(bytes, empty + 28, 3);
                break;
            case "only-empty-loads":
                U32(bytes, Elf + 52 + 16, 0);
                U32(bytes, Elf + 52 + 20, 0);
                break;
            case "entry-only-in-zero-fill":
                U32(bytes, Elf + 52 + 16, 0);
                break;
        }
        var source = new ObservedContent(bytes) { DeclaredLength = 2400L * Sector };

        var result = await Ps2DvdIdentifier.InspectAsync(source, source.Length);

        result.Status.ShouldBe(Ps2DvdIdentificationStatus.Malformed);
        result.Facts.ShouldBeNull();
        source.MaximumReadEnd.ShouldBe(Elf + 116);
        source.Disposed.ShouldBeFalse();
    }

    [Theory]
    [InlineData("no-cnf")]
    [InlineData("ps1-boot")]
    [InlineData("nested-boot")]
    [InlineData("nonserial-boot")]
    [InlineData("other-device")]
    public async Task InspectAsync_OrdinaryOrUnrecognizedBoot_PreservesUnidentifiedOriginal(string control)
    {
        byte[] bytes = Image();
        switch (control)
        {
            case "no-cnf":
                "OTHERX.CNF;1"u8.CopyTo(bytes.AsSpan(CnfRecord + 33));
                break;
            case "ps1-boot":
                Config(bytes, "BOOT = cdrom:\\SLUS_203.12;1\n");
                break;
            case "nested-boot":
                Config(bytes, "BOOT2=cdrom0:\\DIR\\SLUS_203.12;1\n");
                break;
            case "nonserial-boot":
                Config(bytes, "BOOT2=cdrom0:\\BOOT.ELF;1\n");
                break;
            case "other-device":
                Config(bytes, "BOOT2=host:\\SLUS_203.12;1\n");
                break;
        }
        var result = await Inspect(bytes);
        result.Status.ShouldBe(Ps2DvdIdentificationStatus.NotIdentified);
        result.Facts.ShouldBeNull();
    }

    [Theory]
    [InlineData(0x18, 0x5D1C9EA3U)]
    [InlineData(0x1C, 0xC2339F3DU)]
    public async Task InspectAsync_NintendoMagic_EvenWithPs2ShapedTailDoesNotClaimPs2(int offset, uint magic)
    {
        byte[] bytes = Image();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), magic);
        var source = new ObservedContent(bytes);
        (await Ps2DvdIdentifier.InspectAsync(source, 1L << 20)).Status.ShouldBe(Ps2DvdIdentificationStatus.NotIdentified);
        source.RequestedBytes.ShouldBe(32);
    }

    [Fact]
    public async Task InspectAsync_NotIso_DoesNotReadRootOrInitializeDecoder()
    {
        byte[] bytes = Image();
        bytes[Pvd + 1] = (byte)'X';
        var source = new ObservedContent(bytes);
        (await Ps2DvdIdentifier.InspectAsync(source, 1L << 20)).Status.ShouldBe(Ps2DvdIdentificationStatus.NotIdentified);
        source.RequestedBytes.ShouldBe(32 + Sector);
        source.Disposed.ShouldBeFalse();
    }

    [Fact]
    public async Task InspectAsync_LogicalLimitExceeded_ReadsNothing()
    {
        var source = new ObservedContent(Image());
        (await Ps2DvdIdentifier.InspectAsync(source, source.Length - 1)).Status.ShouldBe(Ps2DvdIdentificationStatus.LimitExceeded);
        source.Reads.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectAsync_ProfileCeiling_AppliesEvenWithLargerAdmission(bool exceeds)
    {
        var source = new ObservedContent(Image())
        {
            DeclaredLength = Ps2DvdIdentifier.MaximumLogicalBytes + (exceeds ? Sector : 0)
        };

        var result = await Ps2DvdIdentifier.InspectAsync(source, 16L << 30);

        result.Status.ShouldBe(exceeds ? Ps2DvdIdentificationStatus.LimitExceeded : Ps2DvdIdentificationStatus.Identified);
        if (exceeds)
        { source.Reads.ShouldBe(0); result.Facts.ShouldBeNull(); }
        else
            result.Facts!.LogicalBytes.ShouldBe(source.Length);
        source.Disposed.ShouldBeFalse();
    }

    [Fact]
    public async Task InspectAsync_NonSectorAlignedLogicalImage_IsMalformedWithoutReads()
    {
        var source = new ObservedContent(Image()) { DeclaredLength = 64 * Sector - 1 };
        (await Ps2DvdIdentifier.InspectAsync(source, 1L << 20)).Status.ShouldBe(Ps2DvdIdentificationStatus.Malformed);
        source.Reads.ShouldBe(0);
    }

    [Theory]
    [InlineData("volume-endian")]
    [InlineData("set-endian")]
    [InlineData("sequence-endian")]
    [InlineData("block-endian")]
    [InlineData("path-size-endian")]
    [InlineData("volume-too-large")]
    [InlineData("multi-volume")]
    [InlineData("wrong-block-size")]
    [InlineData("path-outside")]
    [InlineData("duplicate-pvd")]
    public async Task InspectAsync_InvalidPrimaryGeometry_RejectsBeforeRootRead(string control)
    {
        byte[] bytes = Image();
        switch (control)
        {
            case "volume-endian":
                bytes[Pvd + 84] ^= 1;
                break;
            case "set-endian":
                bytes[Pvd + 122] ^= 1;
                break;
            case "sequence-endian":
                bytes[Pvd + 126] ^= 1;
                break;
            case "block-endian":
                bytes[Pvd + 130] ^= 1;
                break;
            case "path-size-endian":
                bytes[Pvd + 136] ^= 1;
                break;
            case "volume-too-large":
                Both32(bytes, Pvd + 80, uint.MaxValue);
                break;
            case "multi-volume":
                Both16(bytes, Pvd + 120, 2);
                break;
            case "wrong-block-size":
                Both16(bytes, Pvd + 128, 512);
                break;
            case "path-outside":
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(Pvd + 140), uint.MaxValue);
                break;
            case "duplicate-pvd":
                bytes.AsSpan(Pvd, Sector).CopyTo(bytes.AsSpan(Pvd + Sector));
                Descriptor(bytes, 18, 255);
                break;
        }
        var source = new ObservedContent(bytes);
        (await Ps2DvdIdentifier.InspectAsync(source, 1L << 20)).Status.ShouldBe(Ps2DvdIdentificationStatus.Malformed);
        source.MaximumReadEnd.ShouldBeLessThanOrEqualTo((control == "duplicate-pvd" ? 19 : 18) * Sector);
    }

    [Fact]
    public async Task InspectAsync_DescriptorBudgetWithoutTerminator_StopsAtBound()
    {
        byte[] bytes = Image(160);
        for (int block = 17; block < 80; block++)
            Descriptor(bytes, block, 2);
        var source = new ObservedContent(bytes);
        (await Ps2DvdIdentifier.InspectAsync(source, 1L << 20)).Status.ShouldBe(Ps2DvdIdentificationStatus.LimitExceeded);
        source.RequestedBytes.ShouldBe(32 + 64 * Sector);
    }

    [Theory]
    [InlineData("root-endian")]
    [InlineData("root-outside")]
    [InlineData("root-size-endian")]
    [InlineData("root-not-directory")]
    [InlineData("cnf-endian")]
    [InlineData("cnf-outside")]
    [InlineData("cnf-overlaps-root")]
    [InlineData("cnf-directory")]
    [InlineData("boot-outside")]
    [InlineData("boot-overlaps-cnf")]
    [InlineData("boot-truncated-header")]
    [InlineData("duplicate-cnf")]
    [InlineData("duplicate-boot")]
    [InlineData("cnf-version-alias")]
    [InlineData("boot-version-alias")]
    [InlineData("missing-boot")]
    [InlineData("bad-name-length")]
    [InlineData("record-crosses-sector")]
    [InlineData("nonzero-padding")]
    [InlineData("multi-extent-cnf")]
    public async Task InspectAsync_AmbiguousOrMalformedDirectoryAndExtents_Rejects(string control)
    {
        byte[] bytes = Image();
        switch (control)
        {
            case "root-endian":
                bytes[Pvd + 156 + 6] ^= 1;
                break;
            case "root-outside":
                Both32(bytes, Pvd + 156 + 2, uint.MaxValue);
                break;
            case "root-size-endian":
                bytes[Pvd + 156 + 14] ^= 1;
                break;
            case "root-not-directory":
                bytes[Pvd + 156 + 25] = 0;
                break;
            case "cnf-endian":
                bytes[CnfRecord + 6] ^= 1;
                break;
            case "cnf-outside":
                Both32(bytes, CnfRecord + 2, uint.MaxValue);
                break;
            case "cnf-overlaps-root":
                Both32(bytes, CnfRecord + 2, 24);
                break;
            case "cnf-directory":
                bytes[CnfRecord + 25] = 2;
                break;
            case "boot-outside":
                Both32(bytes, BootRecord + 2, 64);
                break;
            case "boot-overlaps-cnf":
                Both32(bytes, BootRecord + 2, 25);
                break;
            case "boot-truncated-header":
                Both32(bytes, BootRecord + 10, 51);
                break;
            case "duplicate-cnf":
                bytes.AsSpan(CnfRecord, 46).CopyTo(bytes.AsSpan(BootRecord + 46));
                break;
            case "duplicate-boot":
                bytes.AsSpan(BootRecord, 46).CopyTo(bytes.AsSpan(BootRecord + 46));
                break;
            case "cnf-version-alias":
                bytes.AsSpan(CnfRecord, 46).CopyTo(bytes.AsSpan(BootRecord + 46));
                bytes[BootRecord + 46 + 33 + 11] = (byte)'2';
                break;
            case "boot-version-alias":
                Record(bytes, BootRecord + 46, "SLUS_203.12"u8.ToArray(), 26, 512, 0);
                break;
            case "missing-boot":
                bytes[BootRecord + 33 + 10] = (byte)'3';
                break;
            case "bad-name-length":
                bytes[CnfRecord + 32] = 250;
                break;
            case "record-crosses-sector":
                bytes.AsSpan(Root + 68, Sector - 68).Clear();
                int at = Root + 68;
                foreach (int value in Enumerable.Range(33, 94).Where(value => value is not (>= 97 and <= 122) && value is not (47 or 58 or 92)).Take(57))
                { Record(bytes, at, [(byte)value], 0, 0, 0); at += 34; }
                Record(bytes, at, "TAILX"u8.ToArray(), 0, 0, 0);
                bytes[Root + Sector - 4] = 46;
                break;
            case "nonzero-padding":
                bytes[BootRecord + 46 + 10] = 1;
                break;
            case "multi-extent-cnf":
                bytes[CnfRecord + 25] = 0x80;
                break;
        }
        var result = await Inspect(bytes);
        result.Status.ShouldBe(Ps2DvdIdentificationStatus.Malformed);
        result.Facts.ShouldBeNull();
    }

    [Fact]
    public async Task InspectAsync_OversizedRoot_RejectsBeforeReadingOrAllocatingRoot()
    {
        byte[] bytes = Image(512);
        Both32(bytes, Pvd + 156 + 10, (256 << 10) + Sector);
        var source = new ObservedContent(bytes);
        (await Ps2DvdIdentifier.InspectAsync(source, bytes.Length)).Status.ShouldBe(Ps2DvdIdentificationStatus.LimitExceeded);
        source.RequestedBytes.ShouldBe(32 + 2 * Sector);
    }

    [Fact]
    public async Task InspectAsync_OversizedConfiguration_RejectsBeforeReadingConfiguration()
    {
        byte[] bytes = Image();
        Both32(bytes, CnfRecord + 10, (16 << 10) + 1);
        var source = new ObservedContent(bytes);
        (await Ps2DvdIdentifier.InspectAsync(source, bytes.Length)).Status.ShouldBe(Ps2DvdIdentificationStatus.LimitExceeded);
        source.RequestedBytes.ShouldBe(32 + 3 * Sector);
    }

    [Theory]
    [InlineData("BOOT2=cdrom0:\\SLUS_203.12;1\nBOOT2=cdrom0:\\SLUS_203.12;1\n")]
    [InlineData("BOOT2=cdrom0:\\..\\SLUS_203.12;1\n")]
    [InlineData("BOOT2=cdrom0:/SLUS_203.12;1\n")]
    [InlineData("BOOT2=cdrom0:\\SLUS_203.12;1\0VER=1\n")]
    [InlineData("BOOT2 cdrom0:\\SLUS_203.12;1\n")]
    public async Task InspectAsync_AmbiguousOrUnsafeConfiguration_Rejects(string text)
    {
        byte[] bytes = Image();
        Config(bytes, text);
        (await Inspect(bytes)).Status.ShouldBe(Ps2DvdIdentificationStatus.Malformed);
    }

    [Fact]
    public async Task InspectAsync_NonAsciiConfiguration_RejectsWithoutLossyTextConversion()
    {
        byte[] bytes = Image();
        bytes[Cnf] = 0xFF;
        (await Inspect(bytes)).Status.ShouldBe(Ps2DvdIdentificationStatus.Malformed);
    }

    [Fact]
    public async Task InspectAsync_OversizedObservedBootValue_Rejects()
    {
        byte[] bytes = Image();
        Config(bytes, "BOOT2=" + new string('a', 129) + "\n");
        (await Inspect(bytes)).Status.ShouldBe(Ps2DvdIdentificationStatus.Malformed);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 2)]
    [InlineData(5, 2)]
    [InlineData(16, 1)]
    [InlineData(18, 3)]
    public async Task InspectAsync_OrdinaryOrWrongElfKind_DoesNotClaimPs2(int offset, byte value)
    {
        byte[] bytes = Image();
        bytes[Elf + offset] = value;
        (await Inspect(bytes)).Status.ShouldBe(Ps2DvdIdentificationStatus.NotIdentified);
    }

    [Theory]
    [InlineData("header-size")]
    [InlineData("program-offset-overflow")]
    [InlineData("program-entry-size")]
    [InlineData("no-programs")]
    [InlineData("entry-misaligned")]
    [InlineData("entry-outside")]
    [InlineData("segment-outside-file")]
    [InlineData("segment-file-larger-memory")]
    [InlineData("segment-memory-overflow")]
    [InlineData("segment-not-executable")]
    [InlineData("bad-alignment")]
    [InlineData("section-table-outside")]
    public async Task InspectAsync_MalformedMipsElfBounds_Rejects(string control)
    {
        byte[] bytes = Image();
        switch (control)
        {
            case "header-size":
                U16(bytes, Elf + 40, 51);
                break;
            case "program-offset-overflow":
                U32(bytes, Elf + 28, uint.MaxValue);
                break;
            case "program-entry-size":
                U16(bytes, Elf + 42, 31);
                break;
            case "no-programs":
                U16(bytes, Elf + 44, 0);
                break;
            case "entry-misaligned":
                U32(bytes, Elf + 24, 0x00100001);
                break;
            case "entry-outside":
                U32(bytes, Elf + 24, 0x00200000);
                break;
            case "segment-outside-file":
                U32(bytes, Elf + 52 + 4, uint.MaxValue);
                break;
            case "segment-file-larger-memory":
                U32(bytes, Elf + 52 + 20, 127);
                break;
            case "segment-memory-overflow":
                U32(bytes, Elf + 52 + 8, uint.MaxValue - 15);
                break;
            case "segment-not-executable":
                U32(bytes, Elf + 52 + 24, 4);
                break;
            case "bad-alignment":
                U32(bytes, Elf + 52 + 28, 3);
                break;
            case "section-table-outside":
                U16(bytes, Elf + 46, 40);
                U16(bytes, Elf + 48, 1);
                U32(bytes, Elf + 32, uint.MaxValue);
                break;
        }
        (await Inspect(bytes)).Status.ShouldBe(Ps2DvdIdentificationStatus.Malformed);
    }

    [Fact]
    public async Task InspectAsync_ExcessiveProgramHeaderCount_RejectsBeforeProgramRead()
    {
        byte[] bytes = Image();
        U16(bytes, Elf + 44, 129);
        var source = new ObservedContent(bytes);
        (await Ps2DvdIdentifier.InspectAsync(source, bytes.Length)).Status.ShouldBe(Ps2DvdIdentificationStatus.LimitExceeded);
        source.MaximumReadEnd.ShouldBe(Elf + 52);
    }

    [Fact]
    public async Task InspectAsync_DeclaredSnapshotTruncatesDuringRead_ReturnsMalformedAndBorrowsSource()
    {
        var source = new ObservedContent(Image()) { EofAfterRead = 3 };
        (await Ps2DvdIdentifier.InspectAsync(source, 1L << 20)).Status.ShouldBe(Ps2DvdIdentificationStatus.Malformed);
        source.Disposed.ShouldBeFalse();
    }

    [Fact]
    public async Task InspectAsync_ChangedLogicalLength_DoesNotReturnFacts()
    {
        var source = new ObservedContent(Image()) { ChangeLengthAfterRead = 4 };
        var result = await Ps2DvdIdentifier.InspectAsync(source, 1L << 20);
        result.Status.ShouldBe(Ps2DvdIdentificationStatus.Malformed);
        result.Facts.ShouldBeNull();
    }

    [Fact]
    public async Task InspectAsync_SourceIoFailure_PropagatesAndDoesNotDispose()
    {
        var failure = new IOException("owned source failure");
        var source = new ObservedContent(Image()) { FailureAfterRead = 2, Failure = failure };
        var actual = await Should.ThrowAsync<IOException>(() => Ps2DvdIdentifier.InspectAsync(source, 1L << 20));
        actual.ShouldBeSameAs(failure);
        source.Disposed.ShouldBeFalse();
    }

    [Fact]
    public async Task InspectAsync_DecoderIntegrityFailure_PropagatesWithoutReclassifying()
    {
        var failure = new InvalidDataException("decoded frame rejected");
        var source = new ObservedContent(Image()) { FailureAfterRead = 2, Failure = failure };
        var actual = await Should.ThrowAsync<InvalidDataException>(() => Ps2DvdIdentifier.InspectAsync(source, 1L << 20));
        actual.ShouldBeSameAs(failure);
        source.Disposed.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectAsync_Cancellation_PropagatesAndDoesNotDispose(bool duringRead)
    {
        using var cancellation = new CancellationTokenSource();
        var source = new ObservedContent(Image()) { AfterRead = duringRead ? cancellation.Cancel : null };
        if (!duringRead)
            cancellation.Cancel();
        try
        {
            await Ps2DvdIdentifier.InspectAsync(source, 1L << 20, cancellation.Token);
            throw new InvalidOperationException("Cancellation must propagate.");
        }
        catch (OperationCanceledException error) { error.CancellationToken.ShouldBe(cancellation.Token); }
        source.Disposed.ShouldBeFalse();
        if (!duringRead)
            source.Reads.ShouldBe(0);
    }

    private static Task<Ps2DvdIdentificationResult> Inspect(byte[] bytes) =>
        Ps2DvdIdentifier.InspectAsync(new ObservedContent(bytes), Math.Max(bytes.Length, 1));

    private static void SetCompactRootBytes(byte[] bytes, uint count)
    {
        Both32(bytes, Pvd + 156 + 10, count);
        Both32(bytes, Root + 10, count);
        Both32(bytes, Root + 34 + 10, count);
    }

    private static void SetCompatibilityRoot864(byte[] bytes, uint bootBytes)
    {
        // Independently generated record sizes reproduce the measured 864-byte root; filler files have no data.
        bytes.AsSpan(Root, Sector).Clear();
        Record(bytes, Pvd + 156, [0], 24, 864, 2);
        Record(bytes, Root, [0], 24, 864, 2);
        bytes[Root] = 48;
        Record(bytes, Root + 48, [1], 24, 864, 2);
        bytes[Root + 48] = 48;
        Record(bytes, Root + 96, "SLUS_203.12;1"u8.ToArray(), 26, bootBytes, 0);
        bytes[Root + 96] = 60;
        Record(bytes, Root + 156, "SYSTEM.CNF;1"u8.ToArray(), 25, (uint)Boot2.Length, 0);
        bytes[Root + 156] = 60;
        int at = Root + 216;
        int[] lengths = [60, 58, 62, 58, 60, 60, 58, 58, 56, 56, 62];
        for (int index = 0; index < lengths.Length; index++)
        {
            Record(bytes, at, Encoding.ASCII.GetBytes($"EXTRA{index:D2};1"), 0, 0, 0);
            bytes[at] = (byte)lengths[index];
            at += lengths[index];
        }
        at.ShouldBe(Root + 864);
    }

    private static byte[] CompatibilityEmptyLoadImage()
    {
        // Declared sparse logical extent; only the synthetic boot header/table exist in the borrowed backing array.
        byte[] bytes = Image();
        Both32(bytes, Pvd + 80, 2400);
        Both32(bytes, BootRecord + 10, 4615960);
        U32(bytes, Elf + 24, 0x00100008);
        U16(bytes, Elf + 44, 2);
        int load = Elf + 52;
        U32(bytes, load + 4, 4096);
        U32(bytes, load + 8, 0x00100000);
        U32(bytes, load + 12, 0x00100000);
        U32(bytes, load + 16, 4589836);
        U32(bytes, load + 20, 6791336);
        U32(bytes, load + 24, 7);
        U32(bytes, load + 28, 4096);
        int empty = load + 32;
        U32(bytes, empty, 1);
        U32(bytes, empty + 4, 4598204);
        U32(bytes, empty + 8, 0);
        U32(bytes, empty + 12, 0x02000000);
        U32(bytes, empty + 16, 0);
        U32(bytes, empty + 20, 0);
        U32(bytes, empty + 24, 7);
        U32(bytes, empty + 28, 4096);
        return bytes;
    }

    private static byte[] Image(int sectors = 64)
    {
        byte[] bytes = new byte[sectors * Sector];
        Descriptor(bytes, 16, 1);
        Descriptor(bytes, 17, 255);
        Both32(bytes, Pvd + 80, (uint)sectors);
        Both16(bytes, Pvd + 120, 1);
        Both16(bytes, Pvd + 124, 1);
        Both16(bytes, Pvd + 128, Sector);
        Both32(bytes, Pvd + 132, 10);
        U32(bytes, Pvd + 140, 20);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(Pvd + 148), 21);
        bytes[Pvd + 881] = 1;
        Record(bytes, Pvd + 156, new byte[] { 0 }, 24, Sector, 2);
        Record(bytes, Root, new byte[] { 0 }, 24, Sector, 2);
        Record(bytes, Root + 34, new byte[] { 1 }, 24, Sector, 2);
        Record(bytes, CnfRecord, "SYSTEM.CNF;1"u8.ToArray(), 25, (uint)Boot2.Length, 0);
        Record(bytes, BootRecord, "SLUS_203.12;1"u8.ToArray(), 26, 512, 0);
        Config(bytes, Boot2);
        bytes[Elf] = 0x7F;
        "ELF"u8.CopyTo(bytes.AsSpan(Elf + 1));
        bytes[Elf + 4] = 1;
        bytes[Elf + 5] = 1;
        bytes[Elf + 6] = 1;
        U16(bytes, Elf + 16, 2);
        U16(bytes, Elf + 18, 8);
        U32(bytes, Elf + 20, 1);
        U32(bytes, Elf + 24, 0x00100000);
        U32(bytes, Elf + 28, 52);
        U16(bytes, Elf + 40, 52);
        U16(bytes, Elf + 42, 32);
        U16(bytes, Elf + 44, 1);
        U32(bytes, Elf + 52, 1);
        U32(bytes, Elf + 52 + 4, 256);
        U32(bytes, Elf + 52 + 8, 0x00100000);
        U32(bytes, Elf + 52 + 16, 128);
        U32(bytes, Elf + 52 + 20, 256);
        U32(bytes, Elf + 52 + 24, 5);
        U32(bytes, Elf + 52 + 28, 16);
        return bytes;
    }

    private static void Descriptor(byte[] bytes, int block, byte type)
    {
        bytes.AsSpan(block * Sector, Sector).Clear();
        bytes[block * Sector] = type;
        "CD001"u8.CopyTo(bytes.AsSpan(block * Sector + 1));
        bytes[block * Sector + 6] = 1;
    }

    private static void Config(byte[] bytes, string text)
    {
        byte[] config = Encoding.ASCII.GetBytes(text);
        bytes.AsSpan(Cnf, Sector).Clear();
        config.CopyTo(bytes, Cnf);
        Both32(bytes, CnfRecord + 10, (uint)config.Length);
    }

    private static void Record(byte[] bytes, int offset, byte[] name, uint block, uint count, byte flags)
    {
        int size = 33 + name.Length + (name.Length % 2 == 0 ? 1 : 0);
        bytes.AsSpan(offset, size).Clear();
        bytes[offset] = (byte)size;
        Both32(bytes, offset + 2, block);
        Both32(bytes, offset + 10, count);
        bytes[offset + 25] = flags;
        Both16(bytes, offset + 28, 1);
        bytes[offset + 32] = (byte)name.Length;
        name.CopyTo(bytes, offset + 33);
    }

    private static void U16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    private static void U32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void Both16(byte[] bytes, int offset, ushort value)
    {
        U16(bytes, offset, value);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 2), value);
    }
    private static void Both32(byte[] bytes, int offset, uint value)
    {
        U32(bytes, offset, value);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 4), value);
    }

    private sealed class ObservedContent(byte[] bytes) : IRandomAccessContent
    {
        public int MaximumReadBytes { get; init; } = int.MaxValue;
        public int EofAfterRead { get; init; } = int.MaxValue;
        public int FailureAfterRead { get; init; } = int.MaxValue;
        public Exception? Failure { get; init; }
        public int ChangeLengthAfterRead { get; init; } = int.MaxValue;
        public long? DeclaredLength { get; init; }
        public Action? AfterRead { get; init; }
        public int Reads { get; private set; }
        public long RequestedBytes { get; private set; }
        public long MaximumReadEnd { get; private set; }
        public long MinimumReadOffset { get; private set; } = long.MaxValue;
        public bool Disposed { get; private set; }
        public long Length => (DeclaredLength ?? bytes.Length) + (Reads >= ChangeLengthAfterRead ? Sector : 0);
        public ContentReadIntegrity Integrity => ContentReadIntegrity.None;
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ct.ThrowIfCancellationRequested();
            Reads++;
            if (Reads >= FailureAfterRead)
                throw Failure ?? new IOException("source failure");
            RequestedBytes += destination.Length;
            MinimumReadOffset = Math.Min(MinimumReadOffset, offset);
            if (Reads > EofAfterRead || offset >= bytes.Length)
                return ValueTask.FromResult(0);
            int count = (int)Math.Min(Math.Min(destination.Length, MaximumReadBytes), bytes.Length - offset);
            bytes.AsMemory((int)offset, count).CopyTo(destination);
            MaximumReadEnd = Math.Max(MaximumReadEnd, offset + count);
            AfterRead?.Invoke();
            return ValueTask.FromResult(count);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    // A logical projection with a different stored prefix proves the abstraction boundary, not CHD decoding.
    private sealed class Projection(ObservedContent source, long start, long length) : IRandomAccessContent
    {
        public long Length => length;
        public ContentReadIntegrity Integrity => source.Integrity;
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default) =>
            source.ReadAtAsync(start + offset, destination[..(int)Math.Min(destination.Length, Math.Max(0, length - offset))], ct);
        public ValueTask DisposeAsync() => source.DisposeAsync();
    }

    // Sparse logical relocation tests arithmetic above 4GiB; it does not represent or qualify a real DVD9 image.
    private sealed class RelocatedView(ObservedContent source) : IRandomAccessContent
    {
        public long Length => (1L << 32) + source.Length;
        public long MaximumReadEnd { get; private set; }
        public ContentReadIntegrity Integrity => source.Integrity;
        public async ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            int count = await source.ReadAtAsync(offset >= (1L << 32) ? offset - (1L << 32) : offset, destination, ct);
            MaximumReadEnd = Math.Max(MaximumReadEnd, offset + count);
            return count;
        }
        public ValueTask DisposeAsync() => source.DisposeAsync();
    }
}
