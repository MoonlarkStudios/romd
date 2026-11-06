using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Romd.Infrastructure;
using Romd.Admin.Application.Source.Rom;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Media.Rvz;
using Romd.Infrastructure.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class RomMediaPolicyTests
{
    [Fact]
    public void Composition_Ps1CdSet_AllowsAutomaticConversionAfterVerifiedInputPublication()
    {
        var kind = RomMediaPolicyComposition.Create().Kinds.Kinds.Single(kind => kind.Kind == "ps1-cd-set");
        kind.ConversionMode.ShouldBe(ConversionMode.Automatic);
    }

    [Theory]
    [InlineData("gc", RomDeliveryFormat.Rvz, "dolphin")]
    [InlineData("wii", RomDeliveryFormat.Rvz, "dolphin")]
    [InlineData("ps2", RomDeliveryFormat.Chd, "pcsx2")]
    public void CreateDeliveryPolicy_DerivesEachPlatformFromItsRegisteredMediaPolicy(string platform,
        RomDeliveryFormat format, string runtime)
    {
        var delivery = RomMediaPolicyComposition.CreateDeliveryPolicy();

        delivery.PlatformKeys.Order(StringComparer.Ordinal).ShouldBe(RomMediaPolicyComposition.CreateRegistry().Policies
            .Select(policy => policy.PlatformKey).Order(StringComparer.Ordinal));
        var stored = delivery.For(platform).ShouldNotBeNull();
        stored.Format.ShouldBe(format);
        stored.NativeRuntimes.ShouldBe([runtime]);
    }

    [Fact]
    public void CreateDeliveryPolicy_FormatCatalog_NamesEachEncodedFormatByItsExtension()
    {
        var formats = RomMediaPolicyComposition.CreateDeliveryPolicy().Formats;

        formats.FileExtension(RomDeliveryFormat.Rvz).ShouldBe(".rvz");
        formats.FileExtension(RomDeliveryFormat.Chd).ShouldBe(".chd");
        Should.Throw<ArgumentOutOfRangeException>(() => formats.FileExtension(RomDeliveryFormat.Original));
    }

    [Theory]
    [InlineData(RomDeliveryFormat.Original, "Original", null)]
    [InlineData(RomDeliveryFormat.Rvz, "RVZ", ".rvz")]
    [InlineData(RomDeliveryFormat.Chd, "CHD", ".chd")]
    public void CreateDeliveryPolicy_FormatCatalog_LabelsEachFormat(RomDeliveryFormat format, string label, string? extension) =>
        RomMediaPolicyComposition.CreateDeliveryPolicy().Formats.Entry(format)
            .ShouldBe(new RomDeliveryFormatEntry(format, label, extension));

    [Theory]
    [InlineData(RomIsoDiscKind.GameCube)]
    [InlineData(RomIsoDiscKind.Wii)]
    public void MaximumOutputBytes_InspectedNintendoKind_UsesUnchangedRecipeAndExactKindBound(RomIsoDiscKind kind)
    {
        const long logical = 1L << 20, limit = 10L << 30;
        var profile = RomMediaPolicyComposition.Create().WholeEncoding[kind == RomIsoDiscKind.GameCube ? "gamecube-disc" : "wii-disc"];
        var expected = kind == RomIsoDiscKind.GameCube
            ? RvzCanonicalBounds.GetGameCubeOutputBound(logical, limit)
            : RvzCanonicalBounds.GetWiiOutputBound(logical, limit);
        profile.DiscKind.ShouldBe(kind);
        profile.MaximumOutputBytes(logical, limit, default).ShouldBe(((RvzCanonicalOutputBound.Success)expected).MaximumOutputBytes);
        var policy = TestRomMediaPolicies.Create().ForDisc(kind).ShouldNotBeNull();
        policy.EncodingProfile.RecipeVersion.ShouldBe(RvzCanonicalRecipe.Id);
        policy.EncodingProfile.StoredEncoding.ShouldBe(RomRepresentationEncoding.Rvz);
        policy.PlatformKey.ShouldBe(kind == RomIsoDiscKind.GameCube ? "gc" : "wii");
    }

    [Fact]
    public void WholeEncoding_RegisteredNintendoProfiles_KeepTheirOwnBoundAndObservedKind()
    {
        var composition = RomMediaPolicyComposition.Create();
        var gc = composition.WholeEncoding["gamecube-disc"];
        var wii = composition.WholeEncoding["wii-disc"];
        gc.DiscKind.ShouldBe(RomIsoDiscKind.GameCube);
        wii.DiscKind.ShouldBe(RomIsoDiscKind.Wii);
        const long logical = 1L << 20, limit = 10L << 30;
        gc.MaximumOutputBytes(logical, limit, default).ShouldBe(new RvzMediaTypePolicy(RvzDiscFamily.GameCube, "gc").EncodingProfile.GetMaximumOutputBytes(logical, limit));
        wii.MaximumOutputBytes(logical, limit, default).ShouldBe(new RvzMediaTypePolicy(RvzDiscFamily.Wii, "wii").EncodingProfile.GetMaximumOutputBytes(logical, limit));
        composition.Registry.ResolveRecipe(RomRepresentationEncoding.Rvz, null).ShouldBe(RvzCanonicalRecipe.Id);
        composition.WholeEncoding.ContainsKey("opaque").ShouldBeFalse();
    }

    [Fact]
    public void ResolveRecipe_UninspectedOriginal_RequiresObservedKindInsteadOfARecipeHint()
    {
        var recipes = TestRomMediaPolicies.Create();
        recipes.ResolveRecipe(RomRepresentationEncoding.Identity, null).ShouldBeNull();
        recipes.ResolveRecipe(RomRepresentationEncoding.Identity, RomIsoDiscKind.GameCube).ShouldBe(RvzCanonicalRecipe.Id);
        recipes.ResolveRecipe((RomRepresentationEncoding)999, null).ShouldBeNull();
        recipes.ResolveRecipe(RomRepresentationEncoding.Rvz, (RomIsoDiscKind)999).ShouldBeNull();
    }

    [Fact]
    public void ResolveRecipe_TestOnlySecondRecipe_RoutesKnownKindsAndRefusesAmbiguousUnknownKind()
    {
        var recipes = new RomMediaPolicyRegistry([
            new Policy(RomIsoDiscKind.GameCube,"gc-current/v1",1000), new Policy(RomIsoDiscKind.Wii,"wii-current/v1",2000)]);
        recipes.ResolveRecipe(RomRepresentationEncoding.Rvz, null).ShouldBeNull();
        recipes.ResolveRecipe(RomRepresentationEncoding.Identity, null).ShouldBeNull();
        recipes.ResolveRecipe(RomRepresentationEncoding.Rvz, RomIsoDiscKind.GameCube).ShouldBe("gc-current/v1");
        recipes.ResolveRecipe(RomRepresentationEncoding.Rvz, RomIsoDiscKind.Wii).ShouldBe("wii-current/v1");
        recipes.ForDisc(RomIsoDiscKind.GameCube).ShouldNotBeNull().EncodingProfile.GetMaximumOutputBytes(128, 10L << 30).ShouldBe(1000);
        recipes.ForDisc(RomIsoDiscKind.Wii).ShouldNotBeNull().EncodingProfile.GetMaximumOutputBytes(128, 10L << 30).ShouldBe(2000);
    }

    [Fact]
    public void Classify_ConflictingPolicyInterpretations_FailsClosed()
    {
        var recipes = new RomMediaPolicyRegistry([
            new Policy(RomIsoDiscKind.GameCube, "gc/v1", 1000, RomRepresentationProfileClass.Canonical),
            new Policy(RomIsoDiscKind.Wii, "wii/v1", 2000, RomRepresentationProfileClass.NonCanonical)]);
        recipes.Classify(new RomContainerFacts.Rvz(0x1000000, 1, 5, 19, 131072, false, 131072), null)
            .ShouldBe(RomRepresentationProfileClass.Unknown);
    }

    [Fact]
    public void Constructor_DuplicateOrInvalidPolicies_RejectsStartup()
    {
        Should.Throw<ArgumentException>(() => new RomMediaPolicyRegistry([]));
        Should.Throw<ArgumentException>(() => new RomMediaPolicyRegistry([
            new Policy(RomIsoDiscKind.GameCube, "one/v1", 100), new Policy(RomIsoDiscKind.GameCube, "two/v1", 200)]));
        Should.Throw<ArgumentException>(() => new RomMediaPolicyRegistry([new Policy((RomIsoDiscKind)999, "one/v1", 100)]));
        Should.Throw<ArgumentException>(() => new RomMediaPolicyRegistry([new Policy(RomIsoDiscKind.GameCube, "bad\n", 100)]));
    }

    [Fact]
    public void MaximumOutputBytes_InvalidLogicalLimitOrCancellation_RefusesReservation()
    {
        var profile = RomMediaPolicyComposition.Create().WholeEncoding["gamecube-disc"];
        profile.MaximumOutputBytes(1L << 20, 128, default).ShouldBe(0);
        profile.MaximumOutputBytes(0, 10L << 30, default).ShouldBe(0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Should.Throw<OperationCanceledException>(() => profile.MaximumOutputBytes(1L << 20, 10L << 30, cancellation.Token));
    }

    [Fact]
    public void AddRomdStorage_SharedComposition_ResolvesRegistryWithoutWorkerExecution()
    {
        var services = new ServiceCollection().AddRomdStorage();
        services.Single(descriptor => descriptor.ServiceType == typeof(RomMediaPolicyRegistry)).ImplementationInstance.ShouldNotBeNull();
        using var provider = services.BuildServiceProvider();
        var recipes = provider.GetRequiredService<RomMediaPolicyRegistry>();
        recipes.Policies.Count.ShouldBe(4);
        recipes.ResolveRecipe(RomRepresentationEncoding.Rvz, null).ShouldBe(RvzCanonicalRecipe.Id);
        provider.GetServices<Romd.Admin.Application.Source.Rom.Reconciliation.IMediaReconciliationExecution>().ShouldBeEmpty();
    }

    [Fact]
    public void WorkerOptions_ConfiguredChdBudgets_KeepSeparateValidatedCpuAndWallLimits()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Romd:Media:ChdEncodeCpuTimeoutSeconds"] = "1300",
            ["Romd:Media:ChdEncodeWallTimeoutSeconds"] = "1400"
        }).Build();
        var options = configuration.GetSection("Romd:Media").Get<MediaProcessingOptions>()!.ValidatedCopy();
        options.ChdEncodeCpuTimeoutSeconds.ShouldBe(1300);
        options.ChdEncodeWallTimeoutSeconds.ShouldBe(1400);
        options.CpuTimeoutSeconds.ShouldBe(600);
        options.WallTimeoutSeconds.ShouldBe(600);
        foreach (int invalid in new[] { 0, 14401 })
        {
            options.ChdEncodeCpuTimeoutSeconds = invalid;
            Should.Throw<InvalidOperationException>(() => options.ValidatedCopy());
            options.ChdEncodeCpuTimeoutSeconds = 1300;
            options.ChdEncodeWallTimeoutSeconds = invalid;
            Should.Throw<InvalidOperationException>(() => options.ValidatedCopy());
            options.ChdEncodeWallTimeoutSeconds = 1400;
        }
    }

    [Theory]
    [InlineData(2048L, 16563L)]
    [InlineData(16384L, 16563L)]
    [InlineData(116736L, 131288L)]
    [InlineData(131072L, 131288L)]
    [InlineData(133120L, 147678L)]
    [InlineData(1048576L, 1049093L)]
    [InlineData(4294967296L, 4296376493L)]
    [InlineData(8589934592L, 8592752813L)]
    [InlineData(10733895680L, 10737417258L)]
    public void ChdOutputBound_WholeOrPartialHunks_ReservesPinnedWriterWorstCase(long logicalBytes, long expected)
    {
        // Literal cases are independently derived from the pinned writer source, including
        // incompressible whole hunks, the DVD metadata, V5 header, and compressed map/tree.
        ChdDvdOutputBounds.GetMaximumOutputBytes(logicalBytes, 10L << 30).ShouldBe(expected);
    }

    [Theory]
    [InlineData(-1L, 10737418240L)]
    [InlineData(0L, 10737418240L)]
    [InlineData(1L, 10737418240L)]
    [InlineData(2047L, 10737418240L)]
    [InlineData(2049L, 10737418240L)]
    [InlineData(long.MaxValue, 10737418240L)]
    [InlineData(10737420288L, 17179869184L)]
    [InlineData(1048576L, 1048575L)]
    [InlineData(2048L, -1L)]
    [InlineData(2048L, 0L)]
    [InlineData(2048L, 128L)]
    [InlineData(2048L, 17179869185L)]
    [InlineData(2048L, long.MaxValue)]
    public void ChdOutputBound_InvalidGeometryOrConfiguredLimit_RefusesBeforeArithmetic(long logicalBytes, long limit)
    {
        ChdDvdOutputBounds.GetMaximumOutputBytes(logicalBytes, limit).ShouldBe(0);
    }

    [Fact]
    public void ChdOutputBound_ValidHostLimitAboveDvdCap_RetainsDvdOutputBound()
    {
        ChdDvdOutputBounds.GetMaximumOutputBytes(1L << 20, 16L << 30).ShouldBe(1049093L);
    }

    [Theory]
    [InlineData(10733897728L)]
    [InlineData(10733912064L)]
    [InlineData(10737418240L)]
    public void ChdOutputBound_GeometryFitsLogicalCapButOutputCannotFit_RefusesReservation(long logicalBytes)
    {
        // The final accepted hunk count is exercised by the independent literal theory.
        // These aligned logical sizes need one more hunk or reach the full logical cap.
        ChdDvdOutputBounds.GetMaximumOutputBytes(logicalBytes, 10L << 30).ShouldBe(0);
    }

    [Fact]
    public void ChdOutputBound_PreCancellation_ThrowsEvenForInvalidGeometry()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Should.Throw<OperationCanceledException>(() => ChdDvdOutputBounds.GetMaximumOutputBytes(2048, 10L << 30, cancellation.Token));
        Should.Throw<OperationCanceledException>(() => ChdDvdOutputBounds.GetMaximumOutputBytes(long.MaxValue, 0, cancellation.Token));
    }

    [Fact]
    public void ChdDvdPolicy_ClosedCandidate_DeclaresOnlyPs2DvdAndExactRecipe()
    {
        var policy = new ChdDvdMediaTypePolicy("ps2");
        policy.DiscKind.ShouldBe(RomIsoDiscKind.PlayStation2Dvd);
        policy.PlatformKey.ShouldBe("ps2");
        policy.EncodingProfile.StoredEncoding.ShouldBe(RomRepresentationEncoding.Chd);
        policy.EncodingProfile.RecipeVersion.ShouldBe("chd-dvd-canonical/v2");
        policy.EncodingProfile.GetMaximumOutputBytes(1L << 20, 10L << 30).ShouldBe(1049093L);
    }

    [Theory]
    [InlineData("canonical", RomRepresentationProfileClass.Canonical)]
    [InlineData("different-hunk", RomRepresentationProfileClass.NonCanonical)]
    [InlineData("different-codec", RomRepresentationProfileClass.NonCanonical)]
    [InlineData("different-metadata", RomRepresentationProfileClass.NonCanonical)]
    [InlineData("identity-container", RomRepresentationProfileClass.Unknown)]
    [InlineData("cd-unit", RomRepresentationProfileClass.Unknown)]
    public void DvdChdProfiles_ObservedContainer_ClassifyScalarsWithoutQualifyingTool(string variant,
        RomRepresentationProfileClass expected)
    {
        RomContainerFacts container = variant switch
        {
            "different-hunk" => Dvd(hunkBytes: 8192),
            "different-codec" => Dvd(codec: 0x7a6c6962),
            "different-metadata" => Dvd(metadata: [new(0x44564420, 1, new byte[] { 1 })]),
            "identity-container" => RomContainerFacts.Identity.Instance,
            "cd-unit" => new RomContainerFacts.Chd(5, 2448 * 16, 2448 * 16, 2448, 1, [0, 0, 0, 0], false,
                Sha1.Parse(new string('a', 40)), Sha1.Parse(new string('b', 40)), [new(0x43485432, 1, "TRACK:1 TYPE:AUDIO"u8)]),
            _ => Dvd()
        };
        IRomMediaEncodingProfile[] profiles = [new ChdDvdMediaTypePolicy("ps2").EncodingProfile, new PspUmdMediaTypePolicy("psp").EncodingProfile];
        foreach (var profile in profiles)
            profile.Classify(container).ShouldBe(expected);
    }

    [Fact]
    public void DvdChdProfiles_SystemBounds_RemainIndependentWithOneRecipe()
    {
        var ps2 = new ChdDvdMediaTypePolicy("ps2").EncodingProfile;
        var psp = new PspUmdMediaTypePolicy("psp").EncodingProfile;
        psp.RecipeVersion.ShouldBe(ps2.RecipeVersion);
        psp.GetMaximumOutputBytes(1L << 20, 10L << 30).ShouldBe(1049093L);
        psp.GetMaximumOutputBytes((2L << 30) + 2048, 10L << 30).ShouldBe(0);
        ps2.GetMaximumOutputBytes((2L << 30) + 2048, 10L << 30).ShouldBeGreaterThan(0);
        foreach (var profile in new[] { ps2, psp })
        {
            profile.GetMaximumOutputBytes(4096, 2048).ShouldBe(0);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Should.Throw<OperationCanceledException>(() => profile.GetMaximumOutputBytes(2048, 10L << 30, cancellation.Token));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void PspPolicy_InvalidPlatform_RefusesBeforeRegistration(string? platform) =>
        Should.Throw<ArgumentException>(() => new PspUmdMediaTypePolicy(platform!));

    [Theory]
    [InlineData(RomIsoDiscKind.PlayStation2Dvd, RomRepresentationEncoding.Identity, RomRepresentationEncoding.Chd)]
    [InlineData(RomIsoDiscKind.PlayStation2Dvd, RomRepresentationEncoding.Chd, RomRepresentationEncoding.Chd)]
    [InlineData(RomIsoDiscKind.GameCube, RomRepresentationEncoding.Identity, RomRepresentationEncoding.Rvz)]
    [InlineData(RomIsoDiscKind.GameCube, RomRepresentationEncoding.Rvz, RomRepresentationEncoding.Rvz)]
    [InlineData(RomIsoDiscKind.Wii, RomRepresentationEncoding.Identity, RomRepresentationEncoding.Rvz)]
    [InlineData(RomIsoDiscKind.Wii, RomRepresentationEncoding.Rvz, RomRepresentationEncoding.Rvz)]
    public void CandidateRegistry_ExplicitKindAndCompatibleEncoding_ResolvesOnlyItsRecipe(RomIsoDiscKind kind,
        RomRepresentationEncoding inputEncoding, RomRepresentationEncoding expectedEncoding)
    {
        var recipes = CandidateRecipes();
        var policy = recipes.ForDisc(kind).ShouldNotBeNull();
        recipes.ResolveRecipe(inputEncoding, kind).ShouldBe(kind == RomIsoDiscKind.PlayStation2Dvd ? "chd-dvd-canonical/v2" : RvzCanonicalRecipe.Id);
        RomMediaPolicyRegistry.IsCompatible(inputEncoding, policy).ShouldBeTrue();
        policy.EncodingProfile.StoredEncoding.ShouldBe(expectedEncoding);
        if (kind == RomIsoDiscKind.PlayStation2Dvd)
            policy.EncodingProfile.GetMaximumOutputBytes(1L << 20, 10L << 30).ShouldBe(1049093L);
        else
            policy.EncodingProfile.GetMaximumOutputBytes(1L << 20, 10L << 30).ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData(RomIsoDiscKind.PlayStation2Dvd, RomRepresentationEncoding.Rvz)]
    [InlineData(RomIsoDiscKind.GameCube, RomRepresentationEncoding.Chd)]
    [InlineData(RomIsoDiscKind.Wii, RomRepresentationEncoding.Chd)]
    public void CandidateRegistry_CrossFormat_RefusesProfileCompatibility(RomIsoDiscKind kind, RomRepresentationEncoding inputEncoding)
    {
        var recipes = CandidateRecipes();
        recipes.ResolveRecipe(inputEncoding, kind).ShouldBeNull();
        RomMediaPolicyRegistry.IsCompatible(inputEncoding, recipes.ForDisc(kind).ShouldNotBeNull()).ShouldBeFalse();
    }

    [Fact]
    public void CandidateRegistry_KnownKinds_ResolvePhysicalProfilesWithoutChoosingAnUnscopedConversion()
    {
        var recipes = CandidateRecipes();
        recipes.ResolveRecipe(RomRepresentationEncoding.Identity, null).ShouldBeNull();
        recipes.ResolveRecipe(RomRepresentationEncoding.Identity, RomIsoDiscKind.PlayStation2Dvd).ShouldBe("chd-dvd-canonical/v2");
        recipes.ResolveRecipe(RomRepresentationEncoding.Identity, RomIsoDiscKind.GameCube).ShouldBe(RvzCanonicalRecipe.Id);
        recipes.ResolveRecipe(RomRepresentationEncoding.Identity, RomIsoDiscKind.Wii).ShouldBe(RvzCanonicalRecipe.Id);
        recipes.ForDisc(RomIsoDiscKind.PlayStation2Dvd).ShouldNotBeNull().PlatformKey.ShouldBe("ps2");
    }

    [Fact]
    public void CandidateRegistry_UninspectedIdentity_DoesNotInferDvdEligibility()
    {
        var recipes = CandidateRecipes();
        recipes.ResolveRecipe(RomRepresentationEncoding.Identity, null).ShouldBeNull();
        recipes.ResolveRecipe(RomRepresentationEncoding.Chd, null).ShouldBe("chd-dvd-canonical/v2");
        recipes.ResolveRecipe(RomRepresentationEncoding.Rvz, null).ShouldBe(RvzCanonicalRecipe.Id);
        recipes.Classify(RomContainerFacts.Identity.Instance, RomIsoDiscKind.PlayStation2Dvd).ShouldBe(RomRepresentationProfileClass.Unknown);
    }

    [Theory]
    [InlineData(RomRepresentationEncoding.Chd)]
    [InlineData(RomRepresentationEncoding.Identity)]
    public void CandidateRegistry_KnownKindUsesCurrentRecipeAndObservedParameters(RomRepresentationEncoding encoding)
    {
        var recipes = CandidateRecipes();
        recipes.ResolveRecipe(encoding, RomIsoDiscKind.PlayStation2Dvd).ShouldBe("chd-dvd-canonical/v2");
        var observed = Dvd(codec: 0x7a6c6962);
        recipes.Classify(observed, RomIsoDiscKind.PlayStation2Dvd).ShouldBe(RomRepresentationProfileClass.NonCanonical);
    }

    private static RomContainerFacts.Chd Dvd(uint hunkBytes = 16384, uint codec = 0x7a737464,
        RomChdMetadataEntry[]? metadata = null) => new(5, 131072, hunkBytes, 2048, 131072 / hunkBytes,
            [codec, 0, 0, 0], false, Sha1.Parse(new string('a', 40)), Sha1.Parse(new string('b', 40)),
            metadata ?? [new(0x44564420, 1, new byte[] { 0 })]);

    private static RomMediaPolicyRegistry CandidateRecipes() => TestRomMediaPolicies.Create();

    private sealed class Policy(RomIsoDiscKind kind, string recipe, long bound,
        RomRepresentationProfileClass classification = RomRepresentationProfileClass.Canonical) : IRomMediaTypePolicy, IRomMediaEncodingProfile
    {
        public IRomMediaEncodingProfile EncodingProfile => this;
        public bool ObservesEncodingLayout(RomLogicalInspection inspection) => false;
        public RomIsoDiscKind DiscKind => kind;
        public string PlatformKey => kind == RomIsoDiscKind.GameCube ? "gc" : "wii";
        public RomRepresentationEncoding StoredEncoding => RomRepresentationEncoding.Rvz;
        public string RecipeVersion => recipe;
        public RomRepresentationProfileClass Classify(RomContainerFacts container) => classification;
        public bool MatchesFacts(RomContentDescriptor content) =>
            new RvzMediaTypePolicy(kind == RomIsoDiscKind.GameCube ? RvzDiscFamily.GameCube : RvzDiscFamily.Wii, PlatformKey).MatchesFacts(content);
        public bool IsEncodingEligible(RomContentDescriptor content, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return MatchesFacts(content);
        }
        public long GetMaximumOutputBytes(long logicalBytes, long maxLogicalBytes, CancellationToken ct = default) => bound;
    }
}
