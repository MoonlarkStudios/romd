using System.Text;
using System.Text.Json.Nodes;
using Romd.Media.Inspection;
using Romd.Media.Nintendo;
using Romd.Media.PlayStation;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests;

public sealed class MediaWorkerProtocolTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("length")]
    [InlineData("volume")]
    [InlineData("identifier")]
    [InlineData("version")]
    [InlineData("nintendo")]
    public void TryRead_PspObservation_RequiresExactBoundedFactsAndExclusiveLogicalLayout(string change)
    {
        var reply = new MediaWorkerReply(MediaWorkerStatus.Inspected, Valid.Identity,
            new(new string('a', 64), 131072, new string('a', 64), new string('c', 40), new string('d', 32), "00000000", 131072,
                new string('0', 64), "identity/v1", Psp: new(131072, 64, "ULUS12345", "1.00")),
            new(NintendoIsoInspectionStatus.Unsupported, NintendoIsoInspector.ProfileVersion));
        var root = JsonNode.Parse(MediaWorkerProtocol.Write(reply))!;
        var measured = root["inspection"]!;
        switch (change)
        {
            case "length":
                measured["pspUmd"]!["logicalBytes"] = 129024;
                break;
            case "volume":
                measured["pspUmd"]!["volumeBlocks"] = 65;
                break;
            case "identifier":
                measured["pspUmd"]!["discId"] = "ULUS/2345";
                break;
            case "version":
                measured["pspUmd"]!["discVersion"] = "a.bb";
                break;
            case "nintendo":
                measured["discKind"] = "gamecube";
                break;
        }
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(root.ToJsonString()), out var parsed).ShouldBe(change == "valid");
        if (change == "valid")
            parsed.ShouldBe(reply);
    }

    private static MediaWorkerReply Valid => new(MediaWorkerStatus.Inspected,
        new(41, 41, 512L << 20, "decoder/mvid:exact", "worker/mvid:exact", null, 512L << 20, 0),
        new(new string('a', 64), 401, new string('b', 64), new string('c', 40), new string('d', 32), "00000000", 1001, new string('0', 64),
            "rvz-import/v1;codec=5;level=19;block=131072;format=01000000;encoder=unknown", RvzParameters: new(0x01000000, 1, 5, 19, 131072, false)));

    [Fact]
    public void WriteRead_CompleteInspection_RoundTripsEveryFactIncludingZeroCrc()
    {
        byte[] output = MediaWorkerProtocol.Write(Valid);
        output.Length.ShouldBeLessThan(MediaWorkerProtocol.MaximumOutputBytes);
        MediaWorkerProtocol.TryRead(output, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(Valid);
    }

    [Fact]
    public void TryRead_RvzKindContradictsCapturedHeader_Rejects()
    {
        var contradictory = Valid with
        {
            Inspection = Valid.Inspection! with
            {
                DiscKind = NintendoDiscKind.GameCube,
                LogicalPrefix = new string('0', 56) + "c2339f3d",
                NintendoLayout = new(0, 0, 0),
                RvzParameters = Valid.Inspection.RvzParameters! with { DiscType = 2 }
            }
        };
        MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(contradictory), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("short-prefix")]
    [InlineData("long-prefix")]
    [InlineData("nonhex-prefix")]
    [InlineData("missing-layout")]
    [InlineData("layout-without-kind")]
    [InlineData("wrong-magic")]
    [InlineData("negative-partitions")]
    [InlineData("excess-partitions")]
    [InlineData("incomplete-partitions")]
    [InlineData("unknown-layout-field")]
    public void TryRead_IncompleteOrContradictoryLogicalObservation_Rejects(string mutation)
    {
        var root = JsonNode.Parse(MediaWorkerProtocol.Write(ValidIso))!;
        var measured = root["inspection"]!;
        switch (mutation)
        {
            case "short-prefix":
                measured["logicalPrefix"] = new string('0', 62);
                break;
            case "long-prefix":
                measured["logicalPrefix"] = new string('0', 66);
                break;
            case "nonhex-prefix":
                measured["logicalPrefix"] = new string('g', 64);
                break;
            case "missing-layout":
                measured["nintendoLayout"] = null;
                break;
            case "layout-without-kind":
                measured["discKind"] = null;
                break;
            case "wrong-magic":
                measured["logicalPrefix"] = new string('0', 64);
                break;
            case "negative-partitions":
                measured["nintendoLayout"]!["partitions"] = -1;
                break;
            case "excess-partitions":
                measured["nintendoLayout"]!["partitions"] = 65;
                break;
            case "incomplete-partitions":
                measured["nintendoLayout"]!["standardKeyPartitions"] = 1;
                break;
            case "unknown-layout-field":
                measured["nintendoLayout"]!["supported"] = true;
                break;
        }
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(root.ToJsonString()), out _).ShouldBeFalse();
    }

    [Fact]
    public void TryRead_WiiPartitionCountsContradictIsoObservation_Rejects()
    {
        var reply = ValidIso with
        {
            Inspection = ValidIso.Inspection! with
            {
                ArtifactBytes = 0x78000,
                LogicalBytes = 0x78000,
                DiscKind = NintendoDiscKind.Wii,
                LogicalPrefix = new string('0', 48) + "5d1c9ea300000000",
                NintendoLayout = new(1, 1, 0)
            },
            IsoInspection = ValidIso.IsoInspection! with { Layout = new(NintendoDiscKind.Wii, 0x78000, 1, 0, 1) }
        };
        MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(reply), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("unknown")]
    [InlineData("missing-field")]
    [InlineData("wrong-type")]
    [InlineData("negative-version")]
    [InlineData("overflow-disc-type")]
    [InlineData("unknown-codec")]
    [InlineData("invalid-block")]
    [InlineData("profile-disagreement")]
    public void TryRead_MissingOrContradictoryRvzParameters_Rejects(string mutation)
    {
        var root = JsonNode.Parse(MediaWorkerProtocol.Write(Valid))!.AsObject();
        var measurement = root["inspection"]!.AsObject();
        var parameters = measurement["rvzParameters"]!.AsObject();
        switch (mutation)
        {
            case "missing":
                measurement.Remove("rvzParameters");
                break;
            case "null":
                measurement["rvzParameters"] = null;
                break;
            case "unknown":
                parameters["unknown"] = 0;
                break;
            case "missing-field":
                parameters.Remove("hasWiiPartitions");
                break;
            case "wrong-type":
                parameters["hasWiiPartitions"] = "false";
                break;
            case "negative-version":
                parameters["version"] = -1;
                break;
            case "overflow-disc-type":
                parameters["discType"] = (long)uint.MaxValue + 1;
                break;
            case "unknown-codec":
                parameters["compression"] = 1;
                break;
            case "invalid-block":
                parameters["blockBytes"] = 65537;
                break;
            case "profile-disagreement":
                parameters["compressionLevel"] = 18;
                break;
        }
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(root.ToJsonString()), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("version")]
    [InlineData("status")]
    [InlineData("pid")]
    [InlineData("session")]
    [InlineData("gcHeapLimitBytes")]
    [InlineData("decoderBuild")]
    [InlineData("workerBuild")]
    [InlineData("linuxLimits")]
    [InlineData("inspection")]
    [InlineData("isoInspection")]
    [InlineData("ps2Inspection")]
    [InlineData("encoding")]
    [InlineData("gcAvailableBytes")]
    [InlineData("gcRegionRangeBytes")]
    [InlineData("job")]
    [InlineData("nativeIdentity")]
    [InlineData("chdInspection")]
    [InlineData("cdInspection")]
    [InlineData("chdRejection")]
    [InlineData("chdEncoding")]
    [InlineData("toolIdentity")]
    [InlineData("chdEncodingRejection")]
    [InlineData("toolExecution")]
    public void TryRead_MissingRequiredEnvelopeField_Rejects(string field)
    {
        var json = JsonNode.Parse(MediaWorkerProtocol.Write(Valid))!.AsObject();
        json.Remove(field);
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(json.ToJsonString()), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("artifactSha256")]
    [InlineData("artifactBytes")]
    [InlineData("logicalSha256")]
    [InlineData("sha1")]
    [InlineData("md5")]
    [InlineData("crc32")]
    [InlineData("logicalBytes")]
    [InlineData("logicalPrefix")]
    [InlineData("nintendoLayout")]
    [InlineData("encodingProfile")]
    [InlineData("discKind")]
    public void TryRead_MissingRequiredMeasurement_Rejects(string field)
    {
        var json = JsonNode.Parse(MediaWorkerProtocol.Write(Valid))!;
        json["inspection"]!.AsObject().Remove(field);
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(json.ToJsonString()), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("trailing")]
    [InlineData("truncated")]
    [InlineData("oversized")]
    [InlineData("numeric-state")]
    [InlineData("bad-version")]
    [InlineData("fractional-length")]
    [InlineData("uppercase-hash")]
    [InlineData("missing-profile")]
    public void TryRead_InvalidOutput_Rejects(string mutation)
    {
        string text = Encoding.UTF8.GetString(MediaWorkerProtocol.Write(Valid));
        text = mutation switch
        {
            "unknown" => text.Insert(1, "\"extra\":true,"),
            "duplicate" => text.Insert(1, "\"version\":\"" + MediaWorkerProtocol.Version + "\","),
            "trailing" => text + "{}",
            "truncated" => text[..^2],
            "oversized" => new string('x', MediaWorkerProtocol.MaximumOutputBytes + 1),
            "numeric-state" => text.Replace("\"inspected\"", "1", StringComparison.Ordinal),
            "bad-version" => text.Replace(MediaWorkerProtocol.Version, "romd-media/v99", StringComparison.Ordinal),
            "fractional-length" => text.Replace("\"logicalBytes\":1001", "\"logicalBytes\":1001.5", StringComparison.Ordinal),
            "uppercase-hash" => text.Replace(new string('a', 64), new string('A', 64), StringComparison.Ordinal),
            "missing-profile" => text.Replace("rvz-import/v1;codec=5", "unknown;codec=5", StringComparison.Ordinal),
            _ => text
        };
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(text), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("codec=5", "codec=6")]
    [InlineData("level=19", "level=2147483648")]
    [InlineData("block=131072", "block=65537")]
    [InlineData("format=01000000", "format=0100000x")]
    [InlineData("encoder=unknown", "encoder=invented")]
    public void TryRead_InvalidContainerFacts_Rejects(string before, string after)
    {
        string text = Encoding.UTF8.GetString(MediaWorkerProtocol.Write(Valid)).Replace(before, after, StringComparison.Ordinal);
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(text), out _).ShouldBeFalse();
    }

    [Fact]
    public void TryRead_AlternateReadableWriterVersion_PreservesActualProfile()
    {
        var alternate = Valid with
        {
            Inspection = Valid.Inspection! with
            {
                EncodingProfile = Valid.Inspection!.EncodingProfile.Replace("01000000", "01010000", StringComparison.Ordinal),
                RvzParameters = Valid.Inspection.RvzParameters! with { Version = 0x01010000 }
            }
        };
        MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(alternate), out var parsed).ShouldBeTrue();
        parsed.ShouldBe(alternate);
    }

    [Theory]
    [InlineData("artifactSha256", 64)]
    [InlineData("logicalSha256", 64)]
    [InlineData("sha1", 40)]
    [InlineData("md5", 32)]
    public void TryRead_EmptyCryptographicIdentity_RejectsBeforeDomainEvidence(string field, int length)
    {
        var json = JsonNode.Parse(MediaWorkerProtocol.Write(Valid))!;
        json["inspection"]![field] = new string('0', length);
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(json.ToJsonString()), out _).ShouldBeFalse();
    }

    private static Ps2DvdObservedFacts ValidPs2Facts => new(64L * 2048, 64, 16, 24, 864,
        25, 80, 26, 256, "cdrom0:\\SLUS_123.45;1", "cdrom0:\\SLUS_123.45;1", "SLUS_123.45", 0x100008, 1);

    private static MediaWorkerReply Ps2Reply(Ps2DvdObservedFacts facts) => Valid with
    {
        Inspection = Valid.Inspection! with
        {
            ArtifactBytes = facts.LogicalBytes,
            LogicalBytes = facts.LogicalBytes,
            LogicalSha256 = Valid.Inspection!.ArtifactSha256,
            EncodingProfile = "identity/v1",
            DiscKind = null,
            RvzParameters = null
        },
        IsoInspection = new(NintendoIsoInspectionStatus.Unsupported, NintendoIsoInspector.ProfileVersion),
        Ps2Inspection = new(Ps2DvdIdentificationStatus.Identified, Ps2DvdIdentifier.ProfileVersion, facts)
    };

    [Theory]
    [InlineData(864U)]
    [InlineData(167U)]
    public void WriteRead_Ps2ShortDeclaredRoot_PreservesEveryFact(uint rootBytes)
    {
        var facts = ValidPs2Facts with { RootDirectoryBytes = rootBytes };
        var reply = Ps2Reply(facts);
        byte[] output = MediaWorkerProtocol.Write(reply);
        output.Length.ShouldBeLessThan(MediaWorkerProtocol.MaximumOutputBytes);
        MediaWorkerProtocol.TryRead(output, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(reply);
        parsed!.Ps2Inspection!.Facts.ShouldBe(facts);
        parsed.Ps2Inspection.Facts!.RootDirectoryBytes.ShouldBe(rootBytes);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("ceiling-plus-one")]
    [InlineData("final-volume-plus-one")]
    [InlineData("cnf-overlap")]
    [InlineData("boot-overlap")]
    public void TryRead_Ps2RootStillRequiresBoundedContainedDisjointExtents(string mutation)
    {
        var facts = mutation switch
        {
            "zero" => ValidPs2Facts with { RootDirectoryBytes = 0 },
            // Other extents remain contained and disjoint, so only the independent root byte ceiling rejects this row.
            "ceiling-plus-one" => ValidPs2Facts with
            {
                LogicalBytes = 512L * 2048,
                VolumeBlocks = 512,
                RootDirectoryBytes = 262145,
                SystemCnfBlock = 160,
                BootExecutableBlock = 180
            },
            "final-volume-plus-one" => ValidPs2Facts with { RootDirectoryBlock = 63, RootDirectoryBytes = 2049 },
            "cnf-overlap" => ValidPs2Facts with { RootDirectoryBytes = 2049 },
            "boot-overlap" => ValidPs2Facts with { RootDirectoryBytes = 4097, SystemCnfBlock = 27 },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        byte[] output = MediaWorkerProtocol.Write(Ps2Reply(facts));
        output.Length.ShouldBeLessThan(MediaWorkerProtocol.MaximumOutputBytes);
        MediaWorkerProtocol.TryRead(output, out var parsed).ShouldBeFalse();
        parsed.ShouldBeNull();
    }

    private static MediaWorkerReply ValidIso => Valid with
    {
        Inspection = Valid.Inspection! with
        {
            LogicalSha256 = Valid.Inspection!.ArtifactSha256,
            ArtifactBytes = 0x6000,
            LogicalBytes = 0x6000,
            EncodingProfile = "identity/v1",
            DiscKind = NintendoDiscKind.GameCube,
            RvzParameters = null,
            LogicalPrefix = new string('0', 56) + "c2339f3d",
            NintendoLayout = new(0, 0, 0)
        },
        IsoInspection = new(NintendoIsoInspectionStatus.Eligible, NintendoIsoInspector.ProfileVersion,
            new(NintendoDiscKind.GameCube, 0x6000, 0, 0, 0))
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteRead_IsoSupportedOrUnsupported_RoundTripsSeparateStructuralFacts(bool supported)
    {
        var reply = supported ? ValidIso : ValidIso with
        {
            Inspection = ValidIso.Inspection! with { ArtifactBytes = 3, LogicalBytes = 3, DiscKind = null, LogicalPrefix = "010203", NintendoLayout = null },
            IsoInspection = new(NintendoIsoInspectionStatus.Unsupported, NintendoIsoInspector.ProfileVersion)
        };

        MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(reply), out var parsed).ShouldBeTrue();

        parsed.ShouldBe(reply);
    }

    [Theory]
    [InlineData("missing-profile")]
    [InlineData("extra-field")]
    [InlineData("missing-layout-field")]
    [InlineData("invalid-kind")]
    [InlineData("invalid-profile")]
    [InlineData("invalid-status")]
    [InlineData("negative-count")]
    [InlineData("gamecube-with-partitions")]
    [InlineData("wii-without-partitions")]
    [InlineData("too-many-partitions")]
    [InlineData("inconsistent-key-counts")]
    [InlineData("layout-length")]
    [InlineData("identity-length")]
    [InlineData("identity-hash")]
    [InlineData("identity-profile")]
    [InlineData("missing-layout")]
    [InlineData("unsupported-with-layout")]
    [InlineData("failure-with-facts")]
    [InlineData("old-version")]
    public void TryRead_InvalidIsoFacts_RejectsBeforeEvidence(string mutation)
    {
        var json = JsonNode.Parse(MediaWorkerProtocol.Write(ValidIso))!;
        var iso = json["isoInspection"]!.AsObject();
        var layout = iso["layout"]!.AsObject();
        switch (mutation)
        {
            case "missing-profile":
                iso.Remove("profile");
                break;
            case "extra-field":
                iso["ticket"] = "unexpected";
                break;
            case "missing-layout-field":
                layout.Remove("kind");
                break;
            case "invalid-kind":
                layout["kind"] = "unknown";
                break;
            case "invalid-profile":
                iso["profile"] = "nintendo-iso/v99";
                break;
            case "invalid-status":
                iso["status"] = "malformed";
                break;
            case "negative-count":
                layout["koreanKeyPartitions"] = -1;
                break;
            case "gamecube-with-partitions":
                layout["wiiPartitionCount"] = 1;
                break;
            case "wii-without-partitions":
                layout["kind"] = "wii";
                break;
            case "too-many-partitions":
                layout["wiiPartitionCount"] = 65;
                break;
            case "inconsistent-key-counts":
                layout["kind"] = "wii";
                layout["logicalBytes"] = 0x78000;
                layout["wiiPartitionCount"] = 1;
                layout["standardKeyPartitions"] = 0;
                layout["koreanKeyPartitions"] = 0;
                break;
            case "layout-length":
                layout["logicalBytes"] = 0x6001;
                break;
            case "identity-length":
                json["inspection"]!["logicalBytes"] = 0x6001;
                break;
            case "identity-hash":
                json["inspection"]!["logicalSha256"] = new string('b', 64);
                break;
            case "identity-profile":
                json["inspection"]!["encodingProfile"] = Valid.Inspection!.EncodingProfile;
                break;
            case "missing-layout":
                iso["layout"] = null;
                break;
            case "unsupported-with-layout":
                iso["status"] = "unsupported";
                break;
            case "failure-with-facts":
                json["status"] = "malformed";
                break;
            case "old-version":
                json["version"] = "romd-media/v1";
                break;
        }

        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(json.ToJsonString()), out _).ShouldBeFalse();
    }

    [Fact]
    public void WriteRead_WiiKoreanPartitionCounts_RoundTripsSupportedLayout()
    {
        var reply = ValidIso with
        {
            Inspection = ValidIso.Inspection! with
            {
                ArtifactBytes = 0x78000,
                LogicalBytes = 0x78000,
                DiscKind = NintendoDiscKind.Wii,
                LogicalPrefix = new string('0', 48) + "5d1c9ea300000000",
                NintendoLayout = new(2, 1, 1)
            },
            IsoInspection = new(NintendoIsoInspectionStatus.Eligible, NintendoIsoInspector.ProfileVersion,
                new(NintendoDiscKind.Wii, 0x78000, 2, 1, 1))
        };

        MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(reply), out var parsed).ShouldBeTrue();

        parsed.ShouldBe(reply);
    }

    private static MediaWorkerReply ValidEncoding => new(MediaWorkerStatus.Encoded, Valid.Identity,
        Encoding: new(new string('a', 64), 0x6000, 0x6000, new string('b', 64), new string('c', 32), new string('d', 40),
            401, "rvz-canonical/v1", "encoder/mvid:actual", MediaWorkerProtocol.CanonicalEncodingProfile, "unix-nice", 10));

    [Fact]
    public void WriteRead_EncodedArtifact_RoundTripsRawFactsWithoutInspectionEvidence()
    {
        MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(ValidEncoding), out var parsed).ShouldBeTrue();
        parsed.ShouldBe(ValidEncoding);
        parsed!.Inspection.ShouldBeNull();
        parsed.IsoInspection.ShouldBeNull();
        MediaWorkerProtocol.Version.ShouldBe("romd-media/v13");
    }

    [Theory]
    [InlineData("sourceSha256")]
    [InlineData("sourceBytes")]
    [InlineData("logicalBytes")]
    [InlineData("artifactSha256")]
    [InlineData("artifactMd5")]
    [InlineData("artifactSha1")]
    [InlineData("artifactBytes")]
    [InlineData("recipeVersion")]
    [InlineData("encoderBuild")]
    [InlineData("encodingProfile")]
    [InlineData("priorityKind")]
    [InlineData("priorityValue")]
    public void TryRead_EncodingFieldMissing_Rejects(string field)
    {
        var json = JsonNode.Parse(MediaWorkerProtocol.Write(ValidEncoding))!;
        json["encoding"]!.AsObject().Remove(field);
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(json.ToJsonString()), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("old-version")]
    [InlineData("inspection-union")]
    [InlineData("failure-union")]
    [InlineData("missing-encoding")]
    [InlineData("unknown-field")]
    [InlineData("empty-hash")]
    [InlineData("uppercase-md5")]
    [InlineData("negative-size")]
    [InlineData("recipe")]
    [InlineData("profile")]
    [InlineData("priority-kind")]
    [InlineData("normal-priority")]
    [InlineData("fractional-priority")]
    public void TryRead_InvalidEncodingUnion_Rejects(string mutation)
    {
        var json = JsonNode.Parse(MediaWorkerProtocol.Write(ValidEncoding))!;
        var encoded = json["encoding"]!.AsObject();
        switch (mutation)
        {
            case "old-version":
                json["version"] = "romd-media/v2";
                break;
            case "inspection-union":
                json["inspection"] = JsonNode.Parse(MediaWorkerProtocol.Write(Valid))!["inspection"]!.DeepClone();
                break;
            case "failure-union":
                json["status"] = "unsupported";
                break;
            case "missing-encoding":
                json["encoding"] = null;
                break;
            case "unknown-field":
                encoded["logicalSha256"] = new string('e', 64);
                break;
            case "empty-hash":
                encoded["sourceSha256"] = new string('0', 64);
                break;
            case "uppercase-md5":
                encoded["artifactMd5"] = new string('A', 32);
                break;
            case "negative-size":
                encoded["artifactBytes"] = -1;
                break;
            case "recipe":
                encoded["recipeVersion"] = "rvz-canonical/v99";
                break;
            case "profile":
                encoded["encodingProfile"] = MediaWorkerProtocol.CanonicalEncodingProfile.Replace("level=19", "level=18", StringComparison.Ordinal);
                break;
            case "priority-kind":
                encoded["priorityKind"] = "normal";
                break;
            case "normal-priority":
                encoded["priorityValue"] = 0;
                break;
            case "fractional-priority":
                encoded["priorityValue"] = 10.5;
                break;
        }
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(json.ToJsonString()), out _).ShouldBeFalse();
    }

    [Fact]
    public void TryRead_DuplicateEncodingField_Rejects()
    {
        string json = Encoding.UTF8.GetString(MediaWorkerProtocol.Write(ValidEncoding));
        json = json.Replace("\"encoding\":{", "\"encoding\":{\"artifactBytes\":401,", StringComparison.Ordinal);
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(json), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("GameCube")]
    [InlineData(" wii")]
    [InlineData("unknown")]
    [InlineData("numeric")]
    public void TryRead_UnrecognizedMeasuredDiscKind_Rejects(string kind)
    {
        var json = JsonNode.Parse(MediaWorkerProtocol.Write(Valid))!;
        json["inspection"]!["discKind"] = kind == "numeric" ? JsonValue.Create(1) : JsonValue.Create(kind);
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(json.ToJsonString()), out _).ShouldBeFalse();
    }

    [Fact]
    public void TryRead_PreKindVersionOrContradictoryIsoKind_Rejects()
    {
        var json = JsonNode.Parse(MediaWorkerProtocol.Write(ValidIso))!;
        json["inspection"]!["discKind"] = "wii";
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(json.ToJsonString()), out _).ShouldBeFalse();
        json["version"] = "romd-media/v3";
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(json.ToJsonString()), out _).ShouldBeFalse();
    }

    private static ChdNativeIdentity Native => new("linux-arm64", "libmoonlark_chdr.so", 1234, new string('a', 64),
        new string('d', 40), "fixture-upstream", 1, "fixture/v1", new string('e', 64), true, true, true);
    private static MediaWorkerReply ValidChd => Valid with
    {
        NativeIdentity = Native,
        Inspection = Valid.Inspection! with { ArtifactBytes = 4096, LogicalBytes = 6144, EncodingProfile = "chd-import/v1", RvzParameters = null },
        ChdInspection = new(5, new(4096, 6144, 4096, 2048, 2, 0x7a6c6962, 0, 0, 0),
            new string('c', 40), new string('c', 40), new string('f', 40), new string('f', 40),
            new("chd-dvd-metadata/v1", 1, 0x44564420, 1, 1, 0,
                "c3ee3c942f2cbc35b3a4bec95ec700c2b9d21d5e90c7016b261504710490b406"))
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteRead_ChdInspectionOrExplicitNativeHandshake_RoundTripsIndependentFacts(bool handshake)
    {
        var reply = handshake ? new MediaWorkerReply(MediaWorkerStatus.Ready, Valid.Identity, NativeIdentity: Native) : ValidChd;
        byte[] output = MediaWorkerProtocol.Write(reply);
        output.Length.ShouldBeLessThan(MediaWorkerProtocol.MaximumOutputBytes);
        MediaWorkerProtocol.TryRead(output, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(reply);
    }

    [Theory]
    [InlineData("osx-arm64")]
    [InlineData("linux-arm64")]
    [InlineData("linux-x64")]
    [InlineData("linux-musl-x64")]
    [InlineData("win-x64")]
    public void WriteReadNativeIdentity_AnyBoundedRidLabel_PreservesRecordedDescriptor(string rid)
    {
        var expected = Native with { Rid = rid };
        var json = MediaWorkerProtocol.WriteNativeIdentity(expected);
        Encoding.UTF8.GetByteCount(json.GetRawText()).ShouldBeLessThan(MediaWorkerProtocol.MaximumNativeIdentityBytes);
        MediaWorkerProtocol.TryReadNativeIdentity(json, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(expected);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("missing-build")]
    [InlineData("missing-recipe")]
    [InlineData("invalid-rid-label")]
    [InlineData("path-rid")]
    [InlineData("numeric-rid")]
    [InlineData("empty-sha")]
    [InlineData("uppercase-pin")]
    [InlineData("zero-pin")]
    [InlineData("zero-recipe")]
    [InlineData("fractional-recipe")]
    [InlineData("parent-file")]
    [InlineData("rooted-file")]
    [InlineData("oversized-binary")]
    [InlineData("fractional-size")]
    [InlineData("false-capability")]
    [InlineData("numeric-capability")]
    [InlineData("oversized-json")]
    [InlineData("duplicate")]
    public void TryReadNativeIdentity_UntrustedDescriptor_Rejects(string mutation)
    {
        var json = JsonNode.Parse(MediaWorkerProtocol.WriteNativeIdentity(Native).GetRawText())!;
        switch (mutation)
        {
            case "unknown":
                json["extra"] = 1;
                break;
            case "missing-build":
                json.AsObject().Remove("buildId");
                break;
            case "missing-recipe":
                json.AsObject().Remove("recipeVersion");
                break;
            case "invalid-rid-label":
                json["rid"] = "Linux-ARM64";
                break;
            case "path-rid":
                json["rid"] = "../linux-arm64";
                break;
            case "numeric-rid":
                json["rid"] = 1;
                break;
            case "empty-sha":
                json["binarySha256"] = new string('0', 64);
                break;
            case "uppercase-pin":
                json["upstreamCommit"] = new string('C', 40);
                break;
            case "zero-pin":
                json["upstreamCommit"] = new string('0', 40);
                break;
            case "zero-recipe":
                json["recipeVersion"] = 0;
                break;
            case "fractional-recipe":
                json["recipeVersion"] = 1.5;
                break;
            case "parent-file":
                json["file"] = "../libmoonlark_chdr.so";
                break;
            case "rooted-file":
                json["file"] = "/usr/lib/libmoonlark_chdr.so";
                break;
            case "oversized-binary":
                json["binaryBytes"] = (32L << 20) + 1;
                break;
            case "fractional-size":
                json["binaryBytes"] = 1234.5;
                break;
            case "false-capability":
                json["verifiesBlockCrc"] = false;
                break;
            case "numeric-capability":
                json["hasSubcode"] = 1;
                break;
        }
        string text = json.ToJsonString();
        if (mutation == "oversized-json")
            text = text.Insert(1, new string(' ', 2048));
        if (mutation == "duplicate")
            text = text.Insert(1, "\"rid\":\"linux-arm64\",");
        using var document = System.Text.Json.JsonDocument.Parse(text);
        MediaWorkerProtocol.TryReadNativeIdentity(document.RootElement, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("no-native")]
    [InlineData("no-header")]
    [InlineData("version")]
    [InlineData("header-type")]
    [InlineData("missing-hunk")]
    [InlineData("unknown-header")]
    [InlineData("artifact-length")]
    [InlineData("logical-length")]
    [InlineData("hunk-count")]
    [InlineData("unit")]
    [InlineData("hunk-size")]
    [InlineData("cd-codec")]
    [InlineData("stored-raw")]
    [InlineData("stored-overall")]
    [InlineData("decoded-raw")]
    [InlineData("profile")]
    [InlineData("nintendo-kind")]
    [InlineData("rvz-native")]
    [InlineData("duplicate-header")]
    public void TryRead_ChdContradictoryOrMalformedEvidence_Rejects(string mutation)
    {
        var json = JsonNode.Parse(MediaWorkerProtocol.Write(ValidChd))!;
        var chd = json["chdInspection"]!;
        var header = chd["header"]!;
        switch (mutation)
        {
            case "no-native":
                json["nativeIdentity"] = null;
                break;
            case "no-header":
                chd.AsObject().Remove("header");
                break;
            case "version":
                chd["version"] = 4;
                break;
            case "header-type":
                chd["header"] = "header";
                break;
            case "missing-hunk":
                header.AsObject().Remove("hunkBytes");
                break;
            case "unknown-header":
                header["unknown"] = 1;
                break;
            case "artifact-length":
                header["artifactBytes"] = 4097;
                break;
            case "logical-length":
                header["logicalBytes"] = 8192;
                break;
            case "hunk-count":
                header["hunkCount"] = 1;
                break;
            case "unit":
                header["unitBytes"] = 2448;
                break;
            case "hunk-size":
                header["hunkBytes"] = 4097;
                break;
            case "cd-codec":
                header["codec0"] = 0x63647a6c;
                break;
            case "stored-raw":
                chd["storedRawSha1"] = new string('a', 40);
                break;
            case "stored-overall":
                chd["storedOverallSha1"] = new string('a', 40);
                break;
            case "decoded-raw":
                json["inspection"]!["sha1"] = new string('a', 40);
                break;
            case "profile":
                json["inspection"]!["encodingProfile"] = Valid.Inspection!.EncodingProfile;
                break;
            case "nintendo-kind":
                json["inspection"]!["discKind"] = "gamecube";
                break;
            case "rvz-native":
                json = JsonNode.Parse(MediaWorkerProtocol.Write(Valid))!;
                json["nativeIdentity"] = JsonNode.Parse(MediaWorkerProtocol.WriteNativeIdentity(Native).GetRawText());
                break;
        }
        string text = json.ToJsonString();
        if (mutation == "duplicate-header")
            text = text.Replace("\"header\":{", "\"header\":{\"hunkCount\":2,", StringComparison.Ordinal);
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(text), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(ChdRejectionStage.Header, "TruncatedHeader", MediaWorkerStatus.Malformed, false, null)]
    [InlineData(ChdRejectionStage.Header, "LogicalLimitExceeded", MediaWorkerStatus.LimitExceeded, false, null)]
    [InlineData(ChdRejectionStage.NativeVerification, "ProcessRidMismatch", MediaWorkerStatus.Unsupported, false, null)]
    [InlineData(ChdRejectionStage.NativeVerification, "BinaryMissing", MediaWorkerStatus.Malformed, false, null)]
    [InlineData(ChdRejectionStage.NativeVerification, "NativeArtifactIo", MediaWorkerStatus.IoFailure, false, null)]
    [InlineData(ChdRejectionStage.NativeLoad, "NativeLoadFailure", MediaWorkerStatus.IoFailure, false, null)]
    [InlineData(ChdRejectionStage.NativeOpen, "NativeOpenFailure", MediaWorkerStatus.Malformed, true, 3)]
    [InlineData(ChdRejectionStage.NativeOpen, "NativeOpenFailure", MediaWorkerStatus.LimitExceeded, true, 2)]
    [InlineData(ChdRejectionStage.Decode, "NativeDecodeFailure", MediaWorkerStatus.Malformed, true, 27)]
    [InlineData(ChdRejectionStage.Decode, "NativeDecodeFailure", MediaWorkerStatus.LimitExceeded, true, 2)]
    [InlineData(ChdRejectionStage.Integrity, "OverallSha1Mismatch", MediaWorkerStatus.Malformed, true, null)]
    [InlineData(ChdRejectionStage.Integrity, "DecodedHunkMismatch", MediaWorkerStatus.Malformed, true, null)]
    [InlineData(ChdRejectionStage.Io, "SourceChanged", MediaWorkerStatus.IoFailure, true, null)]
    public void WriteRead_ChdRejectedAtDifferentStages_PreservesPreciseFailureWithoutMeasurement(
        ChdRejectionStage stage, string cause, MediaWorkerStatus status, bool verified, int? nativeError)
    {
        var reply = new MediaWorkerReply(status, Valid.Identity, NativeIdentity: verified ? Native : null,
            ChdRejection: new(stage, cause, Path.GetFullPath("fixture.chd"), NativeError: nativeError));
        MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(reply), out var parsed).ShouldBeTrue();
        parsed.ShouldBe(reply);
        parsed!.Inspection.ShouldBeNull();
        parsed.ChdInspection.ShouldBeNull();
        var json = JsonNode.Parse(MediaWorkerProtocol.Write(reply))!;
        json["status"] = "inspected";
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(json.ToJsonString()), out _).ShouldBeFalse();
        json = JsonNode.Parse(MediaWorkerProtocol.Write(reply))!;
        json["chdRejection"]!["cause"] = "InventedCause";
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(json.ToJsonString()), out _).ShouldBeFalse();
        if (stage != ChdRejectionStage.Io)
        {
            json = JsonNode.Parse(MediaWorkerProtocol.Write(reply))!;
            json["nativeIdentity"] = verified ? null : JsonNode.Parse(MediaWorkerProtocol.WriteNativeIdentity(Native).GetRawText());
            MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(json.ToJsonString()), out _).ShouldBeFalse();
        }
    }

    [Fact]
    public void TryRead_RetiredDecodedSha1MismatchCause_IsRefused()
    {
        var reply = new MediaWorkerReply(MediaWorkerStatus.Malformed, Valid.Identity, NativeIdentity: Native,
            ChdRejection: new(ChdRejectionStage.Integrity, "RawSha1Mismatch", Path.GetFullPath("fixture.chd")));
        MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(reply), out _).ShouldBeTrue();
        var json = JsonNode.Parse(MediaWorkerProtocol.Write(reply))!;
        json["chdRejection"]!["cause"] = "DecodedSha1Mismatch";

        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(json.ToJsonString()), out _).ShouldBeFalse();
    }

    [Fact]
    public void TryRead_ChdUnsupportedCodec_RequiresActualBoundedSlotAndCodecTogether()
    {
        var reply = new MediaWorkerReply(MediaWorkerStatus.Unsupported, Valid.Identity,
            ChdRejection: new(ChdRejectionStage.Header, "UnsupportedDvdCodec", Path.GetFullPath("fixture.chd"), 2, 0x63647a6c));
        MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(reply), out var parsed).ShouldBeTrue();
        parsed.ShouldBe(reply);
        foreach (string field in new[] { "codecSlot", "codec" })
        {
            var json = JsonNode.Parse(MediaWorkerProtocol.Write(reply))!;
            json["chdRejection"]![field] = null;
            MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(json.ToJsonString()), out _).ShouldBeFalse();
        }
        var outOfRange = JsonNode.Parse(MediaWorkerProtocol.Write(reply))!;
        outOfRange["chdRejection"]!["codecSlot"] = 4;
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(outOfRange.ToJsonString()), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("wrong-stage")]
    [InlineData("wrong-status")]
    [InlineData("missing-native")]
    [InlineData("relative-path")]
    [InlineData("zero-native-error")]
    [InlineData("unknown-native-error")]
    [InlineData("extraneous-codec")]
    [InlineData("extraneous-measurement")]
    public void TryRead_ChdFailureContradictions_Rejects(string mutation)
    {
        var reply = new MediaWorkerReply(MediaWorkerStatus.Malformed, Valid.Identity, NativeIdentity: Native,
            ChdRejection: new(ChdRejectionStage.Decode, "NativeDecodeFailure", Path.GetFullPath("fixture.chd"), NativeError: 14));
        var json = JsonNode.Parse(MediaWorkerProtocol.Write(reply))!;
        var rejection = json["chdRejection"]!;
        switch (mutation)
        {
            case "wrong-stage":
                rejection["stage"] = "header";
                break;
            case "wrong-status":
                json["status"] = "limit";
                break;
            case "missing-native":
                json["nativeIdentity"] = null;
                break;
            case "relative-path":
                rejection["path"] = "relative.chd";
                break;
            case "zero-native-error":
                rejection["nativeError"] = 0;
                break;
            case "unknown-native-error":
                rejection["nativeError"] = 28;
                break;
            case "extraneous-codec":
                rejection["codecSlot"] = 0;
                rejection["codec"] = 0;
                break;
            case "extraneous-measurement":
                json["inspection"] = JsonNode.Parse(MediaWorkerProtocol.Write(ValidChd))!["inspection"]!.DeepClone();
                break;
        }
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(json.ToJsonString()), out _).ShouldBeFalse();
    }
    private static ChdmanToolIdentity Tool => new("linux-arm64", "mame0289", new string('d', 64), 1, 123456, new string('a', 64));
    private static MediaWorkerReply ValidChdEncoding => new(MediaWorkerStatus.Encoded, Valid.Identity,
        ChdEncoding: new(new string('a', 64), 18432, 18432, new string('b', 64), new string('c', 32), new string('d', 40),
            401, new(401, 18432, 16384, 2048, 2, 0x7a737464, 0, 0, 0), ChdDvdCanonicalRecipe.Id,
            ChdDvdCanonicalRecipe.EncoderProfile, "unix-nice", 10), ToolIdentity: Tool, ToolExecution: new(42, 0, 30, 10));

    [Theory]
    [InlineData(2048, 10, 0)]
    [InlineData(18432, 19, 262144)]
    public void WriteRead_ChdEncoding_RoundTripsRawFactsWithJoinedToolAndNoInspection(long sourceBytes, int priority, long diagnosticBytes)
    {
        var artifact = ValidChdEncoding.ChdEncoding!;
        var reply = ValidChdEncoding with
        {
            ChdEncoding = artifact with
            {
                SourceBytes = sourceBytes,
                LogicalBytes = sourceBytes,
                Header = artifact.Header with { LogicalBytes = (ulong)sourceBytes, HunkCount = (uint)((sourceBytes + 16383) / 16384) },
                PriorityValue = priority
            },
            ToolExecution = new(42, 0, diagnosticBytes, 0)
        };
        byte[] wire = MediaWorkerProtocol.Write(reply);
        wire.Length.ShouldBeLessThan(MediaWorkerProtocol.MaximumOutputBytes);
        MediaWorkerProtocol.TryRead(wire, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(reply);
        parsed!.Inspection.ShouldBeNull();
        parsed.IsoInspection.ShouldBeNull();
        parsed.Encoding.ShouldBeNull();
        parsed.NativeIdentity.ShouldBeNull();
        parsed.ChdInspection.ShouldBeNull();
        parsed.ChdRejection.ShouldBeNull();
        string text = Encoding.UTF8.GetString(wire);
        text.ShouldNotContain("Tail");
        text.ShouldNotContain("logicalSha256");
        text.ShouldNotContain("storedRawSha1");
    }

    [Theory]
    [InlineData(ChdDvdCanonicalRecipe.Id)]
    public void WriteRead_ClosedDvdRecipeOperations_PreservesActualIdentityWithoutQualification(string recipe)
    {
        var reply = ValidChdEncoding with { ChdEncoding = ValidChdEncoding.ChdEncoding! with { RecipeVersion = recipe } };

        MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(reply), out var parsed).ShouldBeTrue();

        parsed.ShouldBe(reply);
        parsed!.ChdEncoding!.RecipeVersion.ShouldBe(recipe);
        parsed.ToolIdentity.ShouldBe(Tool);
        parsed.Inspection.ShouldBeNull();
        parsed.ChdInspection.ShouldBeNull();
        parsed.NativeIdentity.ShouldBeNull();
    }

    [Theory]
    [InlineData("chd-dvd-canonical/v1")]
    [InlineData("chd-dvd-canonical/v3")]
    [InlineData("chd-dvd-experiment/v1")]
    [InlineData("chd-dvd-experiment/v2")]
    [InlineData("chd-dvd-canonical/v2;encoder=trusted")]
    [InlineData("arbitrary/v1")]
    public void TryRead_UnsupportedDvdRecipeLabel_RefusesRawSuccess(string recipe)
    {
        var reply = ValidChdEncoding with { ChdEncoding = ValidChdEncoding.ChdEncoding! with { RecipeVersion = recipe } };

        MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(reply), out var parsed).ShouldBeFalse();
        parsed.ShouldBeNull();
    }

    [Theory]
    [InlineData("linux-arm64")]
    [InlineData("linux-x64")]
    [InlineData("osx-arm64")]
    public void WriteReadToolIdentity_RecordedDescriptor_RoundTrips(string rid)
    {
        var expected = Tool with { Rid = rid };
        MediaWorkerProtocol.TryReadToolIdentity(MediaWorkerProtocol.WriteToolIdentity(expected), out var parsed).ShouldBeTrue();
        parsed.ShouldBe(expected);
    }

    [Fact]
    public void TryReadToolIdentity_MissingAnyExpectedFact_Rejects()
    {
        var original = JsonNode.Parse(MediaWorkerProtocol.WriteToolIdentity(Tool).GetRawText())!.AsObject();
        original.Count.ShouldBe(6);
        foreach (string field in original.Select(pair => pair.Key))
        {
            var changed = original.DeepClone().AsObject();
            changed.Remove(field);
            using var document = System.Text.Json.JsonDocument.Parse(changed.ToJsonString());
            MediaWorkerProtocol.TryReadToolIdentity(document.RootElement, out _).ShouldBeFalse(field);
        }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("oversized-json")]
    [InlineData("rid")]
    [InlineData("numeric-rid")]
    [InlineData("zero-binary")]
    [InlineData("large-binary")]
    [InlineData("fractional-binary")]
    [InlineData("empty-hash")]
    [InlineData("uppercase-source")]
    [InlineData("short-source")]
    [InlineData("tag-text")]
    [InlineData("zero-recipe")]
    [InlineData("fractional-recipe")]
    public void TryReadToolIdentity_InvalidOrAmbiguousDescriptor_Rejects(string mutation)
    {
        var json = JsonNode.Parse(MediaWorkerProtocol.WriteToolIdentity(Tool).GetRawText())!;
        switch (mutation)
        {
            case "unknown":
                json["releasePin"] = "approved";
                break;
            case "rid":
                json["rid"] = "Linux-ARM64";
                break;
            case "numeric-rid":
                json["rid"] = 1;
                break;
            case "zero-binary":
                json["binaryBytes"] = 0;
                break;
            case "large-binary":
                json["binaryBytes"] = (64L << 20) + 1;
                break;
            case "fractional-binary":
                json["binaryBytes"] = 1234.5;
                break;
            case "empty-hash":
                json["binarySha256"] = new string('0', 64);
                break;
            case "uppercase-source":
                json["mameSourceSha256"] = new string('C', 64);
                break;
            case "short-source":
                json["mameSourceSha256"] = new string('d', 63);
                break;
            case "tag-text":
                json["mameTag"] = "MAME 0.289";
                break;
            case "zero-recipe":
                json["recipeVersion"] = 0;
                break;
            case "fractional-recipe":
                json["recipeVersion"] = 1.5;
                break;
        }
        string text = json.ToJsonString();
        if (mutation == "duplicate")
            text = text.Insert(1, "\"rid\":\"linux-arm64\",");
        if (mutation == "oversized-json")
            text = text.Insert(1, new string(' ', MediaWorkerProtocol.MaximumToolIdentityBytes));
        using var document = System.Text.Json.JsonDocument.Parse(text);
        MediaWorkerProtocol.TryReadToolIdentity(document.RootElement, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryRead_ChdEncodingMissingAnyArtifactHeaderOrExecutionFact_Rejects()
    {
        var original = JsonNode.Parse(MediaWorkerProtocol.Write(ValidChdEncoding))!;
        foreach (string node in new[] { "chdEncoding", "header", "toolExecution" })
        {
            var fields = (node == "header" ? original["chdEncoding"]![node] : original[node])!.AsObject().Select(pair => pair.Key).ToArray();
            foreach (string field in fields)
            {
                var json = original.DeepClone();
                (node == "header" ? json["chdEncoding"]![node] : json[node])!.AsObject().Remove(field);
                MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(json.ToJsonString()), out _).ShouldBeFalse(node + "/" + field);
            }
        }
    }

    [Theory]
    [InlineData("missing-artifact")]
    [InlineData("missing-tool")]
    [InlineData("missing-execution")]
    [InlineData("failure-status")]
    [InlineData("worker-pid")]
    [InlineData("zero-pid")]
    [InlineData("nonzero-exit")]
    [InlineData("negative-count")]
    [InlineData("fractional-count")]
    [InlineData("diagnostic-overflow")]
    [InlineData("counter-ceiling")]
    [InlineData("source-alignment")]
    [InlineData("source-zero")]
    [InlineData("source-limit")]
    [InlineData("source-logical-mismatch")]
    [InlineData("header-artifact-mismatch")]
    [InlineData("header-logical-mismatch")]
    [InlineData("header-count-mismatch")]
    [InlineData("header-unit")]
    [InlineData("header-hunk")]
    [InlineData("header-codec")]
    [InlineData("historical-v1-codec")]
    [InlineData("historical-v1-reply")]
    [InlineData("extra-codec")]
    [InlineData("recipe")]
    [InlineData("profile")]
    [InlineData("priority")]
    [InlineData("priority-kind")]
    [InlineData("zero-source-hash")]
    [InlineData("uppercase-artifact-hash")]
    [InlineData("unknown-artifact")]
    [InlineData("duplicate-header")]
    [InlineData("duplicate-execution")]
    [InlineData("diagnostic-tail")]
    [InlineData("inspection")]
    [InlineData("iso")]
    [InlineData("rvz")]
    [InlineData("native")]
    [InlineData("chd-inspection")]
    [InlineData("mac-tool")]
    public void TryRead_ChdEncodingContradictionsOrCrossOperationPayloads_Rejects(string mutation)
    {
        var json = JsonNode.Parse(MediaWorkerProtocol.Write(ValidChdEncoding))!;
        var artifact = json["chdEncoding"]!;
        var header = artifact["header"]!;
        var execution = json["toolExecution"]!;
        switch (mutation)
        {
            case "missing-artifact":
                json["chdEncoding"] = null;
                break;
            case "missing-tool":
                json["toolIdentity"] = null;
                break;
            case "missing-execution":
                json["toolExecution"] = null;
                break;
            case "failure-status":
                json["status"] = "malformed";
                break;
            case "worker-pid":
                execution["processId"] = Valid.Identity.ProcessId;
                break;
            case "zero-pid":
                execution["processId"] = 0;
                break;
            case "nonzero-exit":
                execution["exitCode"] = 1;
                break;
            case "negative-count":
                execution["stderrBytes"] = -1;
                break;
            case "fractional-count":
                execution["stdoutBytes"] = 1.5;
                break;
            case "diagnostic-overflow":
                execution["stdoutBytes"] = 262144;
                execution["stderrBytes"] = 1;
                break;
            case "counter-ceiling":
                execution["stdoutBytes"] = (16L << 30) + 1;
                break;
            case "source-alignment":
                artifact["sourceBytes"] = 18431;
                artifact["logicalBytes"] = 18431;
                header["logicalBytes"] = 18431;
                break;
            case "source-zero":
                artifact["sourceBytes"] = 0;
                artifact["logicalBytes"] = 0;
                header["logicalBytes"] = 0;
                header["hunkCount"] = 0;
                break;
            case "source-limit":
                artifact["sourceBytes"] = (16L << 30) + 2048;
                artifact["logicalBytes"] = (16L << 30) + 2048;
                header["logicalBytes"] = (16L << 30) + 2048;
                header["hunkCount"] = ((16L << 30) + 18431) / 16384;
                break;
            case "source-logical-mismatch":
                artifact["logicalBytes"] = 2048;
                header["logicalBytes"] = 2048;
                header["hunkCount"] = 1;
                break;
            case "header-artifact-mismatch":
                header["artifactBytes"] = 402;
                break;
            case "header-logical-mismatch":
                header["logicalBytes"] = 2048;
                break;
            case "header-count-mismatch":
                header["hunkCount"] = 1;
                break;
            case "header-unit":
                header["unitBytes"] = 2448;
                break;
            case "header-hunk":
                header["hunkBytes"] = 32768;
                break;
            case "header-codec":
                header["codec0"] = 0x6c7a6d61;
                break;
            case "historical-v1-codec":
                header["codec0"] = 0x7a6c6962;
                break;
            case "historical-v1-reply":
                artifact["recipeVersion"] = "chd-dvd-canonical/v1";
                artifact["encodingProfile"] = "dvd;unit=2048;hunk=16384;codec=zlib;processors=1";
                header["codec0"] = 0x7a6c6962;
                break;
            case "extra-codec":
                header["codec1"] = 0x7a6c6962;
                break;
            case "recipe":
                artifact["recipeVersion"] = "chd-canonical/v1";
                break;
            case "profile":
                artifact["encodingProfile"] = MediaWorkerProtocol.CanonicalEncodingProfile;
                break;
            case "priority":
                artifact["priorityValue"] = 0;
                break;
            case "priority-kind":
                artifact["priorityKind"] = "windows-priority";
                break;
            case "zero-source-hash":
                artifact["sourceSha256"] = new string('0', 64);
                break;
            case "uppercase-artifact-hash":
                artifact["artifactSha256"] = new string('A', 64);
                break;
            case "unknown-artifact":
                artifact["logicalSha256"] = new string('e', 64);
                break;
            case "diagnostic-tail":
                execution["stdoutTail"] = "untrusted tool text";
                break;
            case "inspection":
                json["inspection"] = JsonNode.Parse(MediaWorkerProtocol.Write(Valid))!["inspection"]!.DeepClone();
                break;
            case "iso":
                json["isoInspection"] = JsonNode.Parse(MediaWorkerProtocol.Write(ValidIso))!["isoInspection"]!.DeepClone();
                break;
            case "rvz":
                json["encoding"] = JsonNode.Parse(MediaWorkerProtocol.Write(ValidEncoding))!["encoding"]!.DeepClone();
                break;
            case "native":
                json["nativeIdentity"] = JsonNode.Parse(MediaWorkerProtocol.WriteNativeIdentity(Native).GetRawText());
                break;
            case "chd-inspection":
                json["chdInspection"] = JsonNode.Parse(MediaWorkerProtocol.Write(ValidChd))!["chdInspection"]!.DeepClone();
                break;
            case "mac-tool":
                json["toolIdentity"]!["rid"] = "osx-arm64";
                break;
        }
        string text = json.ToJsonString();
        if (mutation == "duplicate-header")
            text = text.Replace("\"header\":{", "\"header\":{\"codec0\":2054386788,", StringComparison.Ordinal);
        if (mutation == "duplicate-execution")
            text = text.Replace("\"toolExecution\":{", "\"toolExecution\":{\"exitCode\":0,", StringComparison.Ordinal);
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(text), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(ChdDvdEncodingStage.Source, ChdDvdEncodingCause.SourceHashMismatch, MediaWorkerStatus.Malformed, false)]
    [InlineData(ChdDvdEncodingStage.Source, ChdDvdEncodingCause.UnsupportedSource, MediaWorkerStatus.Unsupported, false)]
    [InlineData(ChdDvdEncodingStage.Source, ChdDvdEncodingCause.SourceChanged, MediaWorkerStatus.IoFailure, true)]
    [InlineData(ChdDvdEncodingStage.ToolLaunch, ChdDvdEncodingCause.LinuxRequired, MediaWorkerStatus.Unsupported, false)]
    [InlineData(ChdDvdEncodingStage.ToolExecution, ChdDvdEncodingCause.ToolExitFailure, MediaWorkerStatus.Malformed, true)]
    [InlineData(ChdDvdEncodingStage.ToolExecution, ChdDvdEncodingCause.DiagnosticLimitExceeded, MediaWorkerStatus.LimitExceeded, true)]
    [InlineData(ChdDvdEncodingStage.ToolExecution, ChdDvdEncodingCause.DrainFailure, MediaWorkerStatus.IoFailure, true)]
    [InlineData(ChdDvdEncodingStage.Output, ChdDvdEncodingCause.InvalidOutputHeader, MediaWorkerStatus.Malformed, true)]
    public void WriteRead_ChdEncodingRejection_PreservesStageAndOptionalJoinedToolFacts(
        ChdDvdEncodingStage stage, ChdDvdEncodingCause cause, MediaWorkerStatus status, bool verified)
    {
        var execution = verified ? new ChdToolExecution(42, cause == ChdDvdEncodingCause.ToolExitFailure ? 1 : 0,
            cause == ChdDvdEncodingCause.DiagnosticLimitExceeded ? 262145 : 10, 0) : null;
        var reply = new MediaWorkerReply(status, Valid.Identity, ToolIdentity: verified ? Tool : null,
            ChdEncodingRejection: new(stage, cause, Path.GetFullPath("fixture.iso")), ToolExecution: execution);
        MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(reply), out var parsed).ShouldBeTrue();
        parsed.ShouldBe(reply);
        parsed!.ChdEncoding.ShouldBeNull();
        parsed.Inspection.ShouldBeNull();
        if (verified)
        {
            reply = reply with { ToolExecution = null };
            MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(reply), out parsed).ShouldBeTrue();
            parsed.ShouldBe(reply);
        }
    }

    [Fact]
    public void WriteRead_SealedToolImageMismatch_CarriesVerifiedIdentityWithoutExecution()
    {
        var reply = new MediaWorkerReply(MediaWorkerStatus.IoFailure, Valid.Identity, ToolIdentity: Tool,
            ChdEncodingRejection: new(ChdDvdEncodingStage.ToolImage, ChdDvdEncodingCause.ToolImageMismatch, Path.GetFullPath("chdman")));
        MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(reply), out var parsed).ShouldBeTrue();
        parsed.ShouldBe(reply);
        // Refused before launch: joined execution facts or a missing verified identity contradict the stage.
        MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(reply with { ToolExecution = new(42, 0, 10, 0) }), out _).ShouldBeFalse();
        MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(reply with { ToolIdentity = null }), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("toolimage", "ToolImageUnavailable")]
    [InlineData("toolimage", "ToolLimitsMismatch")]
    [InlineData("toolimage", "ToolPriorityMismatch")]
    public void TryRead_RetiredPostLaunchObservationCauses_Rejects(string stage, string cause)
    {
        var reply = new MediaWorkerReply(MediaWorkerStatus.IoFailure, Valid.Identity, ToolIdentity: Tool,
            ChdEncodingRejection: new(ChdDvdEncodingStage.ToolImage, ChdDvdEncodingCause.ToolImageMismatch, Path.GetFullPath("chdman")));
        var json = JsonNode.Parse(MediaWorkerProtocol.Write(reply))!;
        json["chdEncodingRejection"]!["stage"] = stage;
        json["chdEncodingRejection"]!["cause"] = cause;
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(json.ToJsonString()), out _).ShouldBeFalse();
    }

    [Fact]
    public void WriteRead_ChdEncodingCandidateRejection_PreservesTypedCauseWithoutToolOrArtifact()
    {
        string path = Path.GetFullPath("chdman");
        var reply = new MediaWorkerReply(MediaWorkerStatus.Malformed, Valid.Identity,
            ChdEncodingRejection: new(ChdDvdEncodingStage.ToolVerification, ChdDvdEncodingCause.CandidateRejected, path,
                new(ChdRejectionStage.NativeVerification, "BinarySha256Mismatch", path)));
        MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(reply), out var parsed).ShouldBeTrue();
        parsed.ShouldBe(reply);
    }

    [Theory]
    [InlineData("stage")]
    [InlineData("cause")]
    [InlineData("status")]
    [InlineData("missing-candidate")]
    [InlineData("candidate-path")]
    [InlineData("candidate-stage")]
    [InlineData("candidate-resident")]
    [InlineData("candidate-identity")]
    [InlineData("candidate-execution")]
    [InlineData("relative-path")]
    [InlineData("unknown-rejection")]
    [InlineData("duplicate-rejection")]
    [InlineData("success-artifact")]
    [InlineData("inspection")]
    [InlineData("count-without-overflow")]
    [InlineData("exit-without-error")]
    [InlineData("missing-verified-identity")]
    public void TryRead_ChdEncodingRejectionContradictions_Rejects(string mutation)
    {
        string path = Path.GetFullPath("chdman");
        var reply = new MediaWorkerReply(MediaWorkerStatus.Malformed, Valid.Identity,
            ChdEncodingRejection: new(ChdDvdEncodingStage.ToolVerification, ChdDvdEncodingCause.CandidateRejected, path,
                new(ChdRejectionStage.NativeVerification, "BinarySha256Mismatch", path)));
        var json = JsonNode.Parse(MediaWorkerProtocol.Write(reply))!;
        var rejected = json["chdEncodingRejection"]!;
        switch (mutation)
        {
            case "stage":
                rejected["stage"] = "source";
                break;
            case "cause":
                rejected["cause"] = "27";
                break;
            case "status":
                json["status"] = "unsupported";
                break;
            case "missing-candidate":
                rejected["candidateRejection"] = null;
                break;
            case "candidate-path":
                rejected["candidateRejection"]!["path"] = Path.GetFullPath("other-chdman");
                break;
            case "candidate-stage":
                rejected["candidateRejection"]!["stage"] = "header";
                break;
            case "candidate-resident":
                rejected["candidateRejection"]!["cause"] = "ResidentBuildMismatch";
                break;
            case "candidate-identity":
                json["toolIdentity"] = JsonNode.Parse(MediaWorkerProtocol.WriteToolIdentity(Tool).GetRawText());
                break;
            case "candidate-execution":
                json["toolExecution"] = JsonNode.Parse(MediaWorkerProtocol.Write(ValidChdEncoding))!["toolExecution"]!.DeepClone();
                break;
            case "relative-path":
                rejected["path"] = "relative";
                break;
            case "unknown-rejection":
                rejected["diagnosticTail"] = "text";
                break;
            case "success-artifact":
                json["chdEncoding"] = JsonNode.Parse(MediaWorkerProtocol.Write(ValidChdEncoding))!["chdEncoding"]!.DeepClone();
                break;
            case "inspection":
                json["inspection"] = JsonNode.Parse(MediaWorkerProtocol.Write(Valid))!["inspection"]!.DeepClone();
                break;
            case "count-without-overflow":
            case "exit-without-error":
            case "missing-verified-identity":
                rejected["stage"] = "toolexecution";
                rejected["candidateRejection"] = null;
                rejected["cause"] = mutation == "count-without-overflow" ? "DiagnosticLimitExceeded" : "ToolExitFailure";
                json["status"] = mutation == "count-without-overflow" ? "limit" : "malformed";
                json["toolIdentity"] = mutation == "missing-verified-identity" ? null : JsonNode.Parse(MediaWorkerProtocol.WriteToolIdentity(Tool).GetRawText());
                json["toolExecution"] = JsonNode.Parse(MediaWorkerProtocol.Write(ValidChdEncoding))!["toolExecution"]!.DeepClone();
                if (mutation == "missing-verified-identity")
                    json["toolExecution"]!["exitCode"] = 1;
                break;
        }
        string text = json.ToJsonString();
        if (mutation == "duplicate-rejection")
            text = text.Replace("\"chdEncodingRejection\":{", "\"chdEncodingRejection\":{\"stage\":\"source\",", StringComparison.Ordinal);
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(text), out _).ShouldBeFalse();
    }

    [Fact]
    public void WriteRead_OtherOperations_KeepEveryChdEncodingFieldNull()
    {
        foreach (var reply in new[] { Valid, ValidIso, ValidEncoding, ValidChd,
            new MediaWorkerReply(MediaWorkerStatus.Ready, Valid.Identity), new MediaWorkerReply(MediaWorkerStatus.Ready, Valid.Identity, NativeIdentity: Native) })
        {
            MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(reply), out var parsed).ShouldBeTrue();
            parsed.ShouldBe(reply);
            parsed!.ChdEncoding.ShouldBeNull();
            parsed.ToolIdentity.ShouldBeNull();
            parsed.ChdEncodingRejection.ShouldBeNull();
            parsed.ToolExecution.ShouldBeNull();
        }
    }

    [Fact]
    public void WriteRead_ChdDiagnosticBudget_RequiresExactCombinedBoundaryForSuccessAndOverflow()
    {
        // The 256-KiB budget combines both pipes, and the exact ceiling still permits success.
        foreach (long stdout in new long[] { 262143, 262144 })
        {
            const long stderr = 1;
            bool overBudget = stdout + stderr > 262144;
            var encoded = ValidChdEncoding with { ToolExecution = new(42, 0, stdout, stderr) };
            MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(encoded), out var parsed).ShouldBe(!overBudget);
            if (!overBudget)
                parsed.ShouldBe(encoded);

            var rejected = new MediaWorkerReply(MediaWorkerStatus.LimitExceeded, Valid.Identity, ToolIdentity: Tool,
                ChdEncodingRejection: new(ChdDvdEncodingStage.ToolExecution, ChdDvdEncodingCause.DiagnosticLimitExceeded, Path.GetFullPath("chdman")),
                ToolExecution: new(42, 137, stdout, stderr));
            MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(rejected), out parsed).ShouldBe(overBudget);
            if (overBudget)
                parsed.ShouldBe(rejected);
        }
    }

    [Fact]
    public void TryRead_ChdDiagnosticFailure_UsesIndependentCounterCeilingsWithoutSuccessAcceptanceClaim()
    {
        var reply = new MediaWorkerReply(MediaWorkerStatus.LimitExceeded, Valid.Identity, ToolIdentity: Tool,
            ChdEncodingRejection: new(ChdDvdEncodingStage.ToolExecution, ChdDvdEncodingCause.DiagnosticLimitExceeded, Path.GetFullPath("chdman")),
            ToolExecution: new(42, 137, 16L << 30, 16L << 30));
        MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(reply), out var parsed).ShouldBeTrue();
        parsed.ShouldBe(reply);
        foreach (string counter in new[] { "stdoutBytes", "stderrBytes" })
        {
            var json = JsonNode.Parse(MediaWorkerProtocol.Write(reply))!;
            json["toolExecution"]![counter] = (16L << 30) + 1;
            MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(json.ToJsonString()), out _).ShouldBeFalse();
        }
    }

    [Fact]
    public void WriteRead_ChdToolIdentity_DoesNotInventAnExpectedCandidatePin()
    {
        var reply = ValidChdEncoding with { ToolIdentity = Tool with { BinarySha256 = new string('f', 64) } };
        MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(reply), out var parsed).ShouldBeTrue();
        parsed.ShouldBe(reply);
        // Matching this transported actual tuple to the caller's expectation belongs to the supervisor.
        parsed!.ToolIdentity.ShouldNotBe(Tool);
    }

    private static MediaWorkerReply ValidChdExtraction => ValidChdEncoding with
    {
        ChdEncoding = null,
        ChdExtraction = new(new string('1', 64), 4097, new string('2', 64), new string('3', 40), new string('4', 32),
            "00000000", 133120, ChdDvdEncodingService.ExtractionProfile, "unix-nice", 10)
    };

    [Theory]
    [InlineData(124, 2048)]
    [InlineData(4097, 133120)]
    [InlineData(10737418240, 10737418240)]
    public void WriteRead_ChdExtraction_PreservesDistinctCompressedIdentityAndFullLogicalQuartet(long compressedBytes, long logicalBytes)
    {
        var original = ValidChdExtraction;
        var reply = original with { ChdExtraction = original.ChdExtraction! with { SourceBytes = compressedBytes, LogicalBytes = logicalBytes } };
        byte[] wire = MediaWorkerProtocol.Write(reply);
        wire.Length.ShouldBeLessThan(MediaWorkerProtocol.MaximumOutputBytes);
        MediaWorkerProtocol.TryRead(wire, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(reply);
        parsed!.ChdEncoding.ShouldBeNull();
        parsed.Inspection.ShouldBeNull();
        parsed.ChdInspection.ShouldBeNull();
        parsed.NativeIdentity.ShouldBeNull();
        parsed.Ps2Inspection.ShouldBeNull();
        parsed.ChdExtraction!.SourceSha256.ShouldNotBe(parsed.ChdExtraction.LogicalSha256);
        parsed.ChdExtraction.Crc32.ShouldBe("00000000", "zero CRC32 is a valid measured digest");
    }

    [Fact]
    public void Write_BothChdTransformVariants_RejectsRatherThanSelectingOne()
    {
        var ambiguous = ValidChdEncoding with { ChdExtraction = ValidChdExtraction.ChdExtraction };
        Should.Throw<ArgumentException>(() => MediaWorkerProtocol.Write(ambiguous));
    }

    [Fact]
    public void TryRead_ChdExtraction_MissingAnyClosedField_Rejects()
    {
        var original = JsonNode.Parse(MediaWorkerProtocol.Write(ValidChdExtraction))!;
        foreach (string field in original["chdEncoding"]!.AsObject().Select(entry => entry.Key))
        {
            var missing = original.DeepClone();
            missing["chdEncoding"]!.AsObject().Remove(field);
            MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(missing.ToJsonString()), out _).ShouldBeFalse(field);
        }
    }

    [Theory]
    [InlineData("zero-source")]
    [InlineData("oversized-source")]
    [InlineData("source-hash")]
    [InlineData("source-hash-zero")]
    [InlineData("logical-hash")]
    [InlineData("logical-hash-zero")]
    [InlineData("sha1")]
    [InlineData("md5")]
    [InlineData("crc32")]
    [InlineData("unaligned-logical")]
    [InlineData("oversized-logical")]
    [InlineData("profile")]
    [InlineData("priority")]
    [InlineData("priority-kind")]
    [InlineData("tool-missing")]
    [InlineData("tool-nonlinux")]
    [InlineData("tool-same-pid")]
    [InlineData("tool-exit")]
    [InlineData("tool-output")]
    [InlineData("native")]
    [InlineData("inspection")]
    [InlineData("ps2")]
    [InlineData("mixed-variants")]
    [InlineData("duplicate-profile")]
    [InlineData("extra-root-variant")]
    public void TryRead_ChdExtraction_InvalidOrCrossOperationFacts_Rejects(string mutation)
    {
        var json = JsonNode.Parse(MediaWorkerProtocol.Write(ValidChdExtraction))!;
        var extracted = json["chdEncoding"]!;
        switch (mutation)
        {
            case "zero-source":
                extracted["sourceBytes"] = 0;
                break;
            case "oversized-source":
                extracted["sourceBytes"] = (10L << 30) + 1;
                break;
            case "source-hash":
                extracted["sourceSha256"] = new string('A', 64);
                break;
            case "source-hash-zero":
                extracted["sourceSha256"] = new string('0', 64);
                break;
            case "logical-hash":
                extracted["logicalSha256"] = new string('A', 64);
                break;
            case "logical-hash-zero":
                extracted["logicalSha256"] = new string('0', 64);
                break;
            case "sha1":
                extracted["sha1"] = new string('0', 40);
                break;
            case "md5":
                extracted["md5"] = "short";
                break;
            case "crc32":
                extracted["crc32"] = "ABCDEF00";
                break;
            case "unaligned-logical":
                extracted["logicalBytes"] = 133121;
                break;
            case "oversized-logical":
                extracted["logicalBytes"] = (10L << 30) + 2048;
                break;
            case "profile":
                extracted["profile"] = "identity/v1";
                break;
            case "priority":
                extracted["priorityValue"] = 0;
                break;
            case "priority-kind":
                extracted["priorityKind"] = "windows-priority";
                break;
            case "tool-missing":
                json["toolIdentity"] = null;
                break;
            case "tool-nonlinux":
                json["toolIdentity"]!["rid"] = "osx-arm64";
                break;
            case "tool-same-pid":
                json["toolExecution"]!["processId"] = json["pid"]!.DeepClone();
                break;
            case "tool-exit":
                json["toolExecution"]!["exitCode"] = 1;
                break;
            case "tool-output":
                json["toolExecution"]!["stdoutBytes"] = 262145;
                break;
            case "native":
                json["nativeIdentity"] = JsonNode.Parse(MediaWorkerProtocol.WriteNativeIdentity(Native).GetRawText());
                break;
            case "inspection":
                json["inspection"] = JsonNode.Parse(MediaWorkerProtocol.Write(Valid))!["inspection"]!.DeepClone();
                break;
            case "ps2":
                json["ps2Inspection"] = new JsonObject();
                break;
            case "mixed-variants":
                foreach (var entry in JsonNode.Parse(MediaWorkerProtocol.Write(ValidChdEncoding))!["chdEncoding"]!.AsObject())
                    extracted[entry.Key] = entry.Value?.DeepClone();
                break;
            case "extra-root-variant":
                json["chdExtraction"] = extracted.DeepClone();
                break;
        }
        string text = json.ToJsonString();
        if (mutation == "duplicate-profile")
            text = text.Replace("\"profile\":", "\"profile\":\"chd-dvd-extract/v1\",\"profile\":", StringComparison.Ordinal);
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(text), out _).ShouldBeFalse(mutation);
    }

    [Fact]
    public void TryRead_LegacyChdEncoding_UnalignedCompressedSizeStillRejectsRatherThanBecomingExtraction()
    {
        var json = JsonNode.Parse(MediaWorkerProtocol.Write(ValidChdEncoding))!;
        json["chdEncoding"]!["sourceBytes"] = 4097;
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(json.ToJsonString()), out _).ShouldBeFalse();
    }

    [Fact]
    public void WriteRead_ExtractionShortOutput_RejectionKeepsJoinedToolWithoutAnyMeasuredPayload()
    {
        var reply = new MediaWorkerReply(MediaWorkerStatus.Malformed, Valid.Identity, ToolIdentity: Tool,
            ChdEncodingRejection: new(ChdDvdEncodingStage.Output, ChdDvdEncodingCause.OutputLengthMismatch, Path.GetFullPath("reserved.iso")),
            ToolExecution: new(42, 0, 10, 0));
        MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(reply), out var parsed).ShouldBeTrue();
        parsed.ShouldBe(reply);
        parsed!.ChdExtraction.ShouldBeNull();
        parsed.ChdEncoding.ShouldBeNull();
    }
}
