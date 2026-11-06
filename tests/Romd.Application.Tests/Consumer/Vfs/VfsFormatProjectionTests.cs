using Romd.Application.Common.Content;
using Romd.Application.Tests.Content;
using Romd.Consumer.Application.Vfs;
using Romd.Domain.Source.Rom;
using Romd.Domain.Vfs;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Consumer.Vfs;

public sealed class VfsFormatProjectionTests
{
    [Theory]
    [InlineData("gc")]
    [InlineData("wii")]
    public void Project_StoredSupportedAvailableRvz_ChangesOnlyEligibleMemberLeaf(string systemKey)
    {
        var result = Project([
            new("Disc/game.iso", false, true), new("Disc/readme.txt", false, false),
            new("Other/game.iso", false, false)], VfsFormat.Stored, systemKey);

        result.Select(member => member.RelativePath).ShouldBe(["Disc/game.rvz", "Disc/readme.txt", "Other/game.iso"]);
        result.Select(member => member.Format).ShouldBe([RomDeliveryFormat.Rvz, RomDeliveryFormat.Original, RomDeliveryFormat.Original]);
        result[0].SourcePath.ShouldBe("Disc/game.iso");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("psx")]
    [InlineData("GC")]
    [InlineData(" gc")]
    public void Project_UnsupportedStableSystemKey_KeepsOriginal(string? systemKey)
    {
        Project([new("disc.iso", false, true)], VfsFormat.Stored, systemKey)
            .ShouldHaveSingleItem().RelativePath.ShouldBe("disc.iso");
    }

    [Fact]
    public void Project_OriginalRetainedRvz_UsesIsoLeafAndLogicalFormat()
    {
        var result = Project([new("Disc/GAME.RVZ", true, true)], VfsFormat.Original, "gc");
        result.ShouldHaveSingleItem().RelativePath.ShouldBe("Disc/GAME.iso");
        result[0].Format.ShouldBe(RomDeliveryFormat.Original);
        result[0].SourcePath.ShouldBe("Disc/GAME.RVZ");
    }

    [Theory]
    [InlineData("cue")]
    [InlineData("gdi")]
    [InlineData("toc")]
    [InlineData("ccd")]
    [InlineData("m3u")]
    [InlineData("m3u8")]
    [InlineData("mds")]
    [InlineData("pls")]
    public void Project_DescriptorAnywhereInCompletePathSet_ForcesEveryMemberOriginal(string extension)
    {
        var result = Project([
            new("Disc/game.rvz", true, true), new("Other/second.iso", false, true),
            new("Lists/launch." + extension.ToUpperInvariant(), false, false)], VfsFormat.Stored, "gc");
        result.Select(member => member.RelativePath).ShouldBe(["Disc/game.iso", "Other/second.iso", "Lists/launch." + extension.ToUpperInvariant()]);
        result.ShouldAllBe(member => member.Format == RomDeliveryFormat.Original);
    }

    [Theory]
    [InlineData("Disc/game.RVZ")]
    [InlineData("Disc/game.rvz/notes.txt")]
    public void Project_StoredRenameCollidesWithFileOrDirectory_FallsBackOnlyAffectedMember(string companion)
    {
        var result = Project([
            new("Disc/game.iso", false, true), new(companion, false, false),
            new("Other/safe.iso", false, true)], VfsFormat.Stored, "gc");

        result.Select(member => member.RelativePath).ShouldBe(["Disc/game.iso", companion, "Other/safe.rvz"]);
        result[0].Format.ShouldBe(RomDeliveryFormat.Original);
        result[2].Format.ShouldBe(RomDeliveryFormat.Rvz);
    }

    [Fact]
    public void Project_TwoStoredNamesCollide_FallsBackBothBeforeFinalValidation()
    {
        var result = Project([
            new("Disc/game.iso", false, true), new("Disc/game.bin", false, true),
            new("Other/safe.iso", false, true)], VfsFormat.Stored, "wii");

        result.Select(member => member.RelativePath).ShouldBe(["Disc/game.iso", "Disc/game.bin", "Other/safe.rvz"]);
    }

    [Fact]
    public void Project_UnicodeNormalizingStoredCollision_FallsBackOnlyConflictingChoices()
    {
        var result = Project([
            new("Disc/é.iso", false, true), new("Disc/e\u0301.bin", false, true),
            new("Other/safe.iso", false, true)], VfsFormat.Stored, "gc");
        result.Select(member => member.RelativePath).ShouldBe(["Disc/é.iso", "Disc/e\u0301.bin", "Other/safe.rvz"]);
    }

    [Fact]
    public void Project_UnportableOriginalSet_PreservesPathsForLayoutRejection()
    {
        var result = Project([
            new("../game.iso", false, true), new("safe.iso", false, true)], VfsFormat.Stored, "gc");
        result.Select(member => member.RelativePath).ShouldBe(["../game.iso", "safe.iso"]);
        result.ShouldAllBe(member => member.Format == RomDeliveryFormat.Original);
    }

    [Fact]
    public void Project_InvalidTypedSelection_ReturnsValidation() =>
        VfsFormatProjection.Project([new("game.iso", false, true)], (VfsFormat)99, TestDeliveryPolicies.Production, "gc")
            .FirstError.ShouldBe(VfsConnectionErrors.InvalidFormat());

    [Fact]
    public void Project_StoredFormatComesFromDeliveryPolicy_NotPlatformKey()
    {
        var policy = new RomDeliveryPolicy(TestDeliveryPolicies.Formats, new MediaKindCatalog([
            TestMediaKinds.Disc("test-disc", "test-platform", RomRepresentationEncoding.Rvz, "rvz-canonical/v1", ["test-only-runtime"],
                ["rvz-import/v1"],content => content.Artifacts.Any(artifact => artifact.HasMeasuredEvidence && artifact.Container is RomContainerFacts.Rvz { DiscType: 1 })),
            TestMediaKinds.Production.Fallback]));
        VfsFormatProjection.Project([new("game.iso", false, true)], VfsFormat.Stored, policy, "test-platform").Value
            .ShouldHaveSingleItem().RelativePath.ShouldBe("game.rvz");
    }

    [Fact]
    public void Project_StoredFormatWithoutNativeRuntime_KeepsOriginal()
    {
        VfsFormatProjection.Project([new("game.iso", false, true)], VfsFormat.Stored, TestDeliveryPolicies.WithoutRuntimes, "gc").Value
            .ShouldHaveSingleItem().Format.ShouldBe(RomDeliveryFormat.Original);
    }

    [Theory]
    [InlineData(".gcm", "game.gcm")]
    [InlineData(null, "game.rvz")]
    public void Project_StoredRvzOriginal_TakesTheRegisteredOriginalExtension(string? originalExtension, string expected)
    {
        var kinds = originalExtension is null
            ? TestMediaKinds.Descriptors.Where(kind => kind.StoredEncoding != RomRepresentationEncoding.Rvz)
            : TestMediaKinds.Descriptors.Select(kind => kind.StoredEncoding == RomRepresentationEncoding.Rvz
                ? kind with { OriginalExtension = originalExtension } : kind);
        var policy = new RomDeliveryPolicy(TestDeliveryPolicies.Formats, new MediaKindCatalog(kinds));

        VfsFormatProjection.Project([new("game.rvz", true, true)], VfsFormat.Original, policy, "gc").Value
            .ShouldHaveSingleItem().RelativePath.ShouldBe(expected);
    }

    private static IReadOnlyList<VfsProjectedMember> Project(IReadOnlyList<VfsReleaseMember> members,
        VfsFormat format, string? systemKey) =>
        VfsFormatProjection.Project(members, format, TestDeliveryPolicies.Production, systemKey).Value;

    [Fact]
    public void Project_UnmeasuredOrUnavailableRvz_DoesNotPromisePreparation()
    {
        Project([new("game.iso", false, false)], VfsFormat.Stored, "gc")
            .ShouldHaveSingleItem().Format.ShouldBe(RomDeliveryFormat.Original);
    }
    [Fact]
    public void Project_StoredAdmittedPs2Chd_ProjectsOnlyAvailableMembers()
    {
        var result = Project([new("Disc/game.iso", false, false, false, true),
            new("Other/manual.bin", false, false)], VfsFormat.Stored, "ps2");
        result.Select(member => member.RelativePath).ShouldBe(["Disc/game.chd", "Other/manual.bin"]);
        result.Select(member => member.Format).ShouldBe([RomDeliveryFormat.Chd, RomDeliveryFormat.Original]);
        result[0].SourcePath.ShouldBe("Disc/game.iso");
    }

    [Fact]
    public void Project_OriginalPreferredChd_UsesLogicalIsoPath()
    {
        var result = Project([new("Disc/GAME.CHD", false, false, true, true)], VfsFormat.Original, "ps2");
        result.ShouldHaveSingleItem().RelativePath.ShouldBe("Disc/GAME.iso");
        result[0].Format.ShouldBe(RomDeliveryFormat.Original);
        result[0].SourcePath.ShouldBe("Disc/GAME.CHD");
    }

    [Theory]
    [InlineData("Disc/game.CHD")]
    [InlineData("Disc/game.chd/notes.txt")]
    public void Project_StoredChdFileOrDirectoryCollision_FallsBackOnlyConflictingMember(string companion)
    {
        var result = Project([new("Disc/game.iso", false, false, false, true),
            new(companion, false, false), new("Other/safe.iso", false, false, false, true)], VfsFormat.Stored, "ps2");
        result.Select(member => member.RelativePath).ShouldBe(["Disc/game.iso", companion, "Other/safe.chd"]);
        result[0].Format.ShouldBe(RomDeliveryFormat.Original);
        result[2].Format.ShouldBe(RomDeliveryFormat.Chd);
    }

    [Theory]
    [InlineData("ps2", false)]
    [InlineData("psx", true)]
    [InlineData("gc", true)]
    public void Project_ChdUnavailableOrWrongPlatform_KeepsOriginal(string system, bool available)
    {
        Project([new("disc.iso", false, false, false, available)], VfsFormat.Stored, system)
            .ShouldHaveSingleItem().Format.ShouldBe(RomDeliveryFormat.Original);
    }

    [Fact]
    public void Project_Ps2Descriptor_DeclinesStoredChdForCompleteSet()
    {
        var result = Project([new("Disc/game.iso", false, false, false, true), new("launch.m3u", false, false)], VfsFormat.Stored, "ps2");
        result.Select(member => member.RelativePath).ShouldBe(["Disc/game.iso", "launch.m3u"]);
        result.ShouldAllBe(member => member.Format == RomDeliveryFormat.Original);
    }

}
